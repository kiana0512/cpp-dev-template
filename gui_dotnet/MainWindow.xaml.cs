using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VhSenderGui.Models;
using VhSenderGui.Services;

namespace VhSenderGui;

public partial class MainWindow
{
    private readonly DispatcherTimer _loopTimer = new();
    private readonly DispatcherTimer _aiFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _aiProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ConcurrentQueue<string> _pendingAiLog = new();
    private readonly ConcurrentQueue<string> _pendingAiProcess = new();
    private readonly ConcurrentQueue<(string Stream, string Line)> _pendingAiParsed = new();
    private DateTime _aiOperationStartedAt;
    private DateTime _lastAiStillLogAt;
    private string _aiCurrentStage = "idle";
    private const int MaxAiTextChars = 200000;
    private const int MaxAiLines = 3000;
    private const int MaxFlushLinesPerTick = 100;
    private readonly AiRuntimeProcessService _aiProcess = new();
    private readonly AiFramesPlaybackService _aiPlayback = new();
    private SenderSession? _session;
    private readonly List<string> _rawJsonLines = [];
    private List<FramePreset> _presets = [];
    private bool _isConnected;
    private bool _isLooping;
    private int _loopTarget;
    private int _loopSent;
    private CancellationTokenSource? _aiGenerateCts;
    private CancellationTokenSource? _aiPlaybackCts;
    private CancellationTokenSource? _aiSetupCts;
    private AiRuntimeResult? _lastAiResult;
    private string? _lastAiFramesPath;
    private string? _lastAiManifestPath;
    private bool _isAiGenerating;
    private bool _isAiPlaying;
    private AiSetupCheckResult? _lastAiSetupCheck;

    public MainWindow()
    {
        InitializeComponent();
        _loopTimer.Tick += LoopTimer_OnTick;
        _aiFlushTimer.Tick += AiFlushTimer_OnTick;
        _aiProgressTimer.Tick += AiProgressTimer_OnTick;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var em in new[] { "neutral", "happy", "sad", "angry", "surprised" })
        {
            EmotionCombo.Items.Add(em);
            AiEmotionCombo.Items.Add(em);
        }
        EmotionCombo.SelectedIndex = 0;
        AiEmotionCombo.SelectedItem = "happy";

        foreach (var ph in new[] { "rest", "a", "i", "u", "e", "o" })
            PhonemeCombo.Items.Add(ph);
        PhonemeCombo.SelectedIndex = 1;

        foreach (var m in new[] { "tcp", "file", "stdout" })
            ModeCombo.Items.Add(m);
        ModeCombo.SelectedIndex = 0;

        foreach (var b in new[] { "dryrun", "real IndexTTS2", "use existing WAV" })
            AiBasicBackendCombo.Items.Add(b);
        AiBasicBackendCombo.SelectedItem = "dryrun";

        foreach (var b in new[] { "dryrun", "auto", "indextts2_local_api", "indextts2_local_cli", "indextts_legacy" })
            AiTtsBackendCombo.Items.Add(b);
        AiTtsBackendCombo.SelectedItem = "dryrun";

        RefreshPathLabels();
        LoadPresetsFromDisk(log: true);
        UpdateSliderLabels();
        SetConnectedState(false);
        SetLoopingState(false);
        ApplyModeUi();
        ApplyAiRuntimeDefaults();
        ApplyAiBasicBackendUi();
        AppendLog("INFO", "VH Sender GUI started.");
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLoopInternal();
        _aiGenerateCts?.Cancel();
        _aiPlaybackCts?.Cancel();
        _aiSetupCts?.Cancel();
        _session?.Dispose();
        _session = null;
    }

    private void RefreshPathLabels()
    {
        PathPresets.Text = "presets.json: " + RepoPaths.PresetsJson;
        PathBlendshape.Text = "blendshape map: " + RepoPaths.BlendshapeMapReference;
        PathOutputJsonl.Text = "default JSONL: " + RepoPaths.DefaultOutputJsonl;
    }

    private void LoadPresetsFromDisk(bool log)
    {
        try
        {
            _presets = PresetLoaderService.LoadFromFile(RepoPaths.PresetsJson);
            if (log) AppendLog("INFO", $"Loaded {_presets.Count} presets.");
        }
        catch (Exception ex)
        {
            _presets = PresetLoaderService.BuiltinPresets();
            if (log) AppendLog("WARN", $"Could not read presets.json: {ex.Message}; using built-ins.");
        }
        PresetList.ItemsSource = null;
        PresetList.ItemsSource = _presets;
        if (_presets.Count > 0) PresetList.SelectedIndex = 0;
    }

    private void ReloadPresetsBtn_OnClick(object sender, RoutedEventArgs e) => LoadPresetsFromDisk(log: true);
    private void RmsSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void ConfSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void FpsSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void HeadPoseSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void ModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyModeUi();

    private void UpdateSliderLabels()
    {
        if (RmsValueLabel == null || ConfValueLabel == null || FpsLabel == null || HeadPoseLabel == null) return;
        RmsValueLabel.Text = RmsSlider.Value.ToString("0.00");
        ConfValueLabel.Text = ConfSlider.Value.ToString("0.00");
        FpsLabel.Text = $"{FpsSlider.Value:0.0} FPS";
        HeadPoseLabel.Text = $"P {HeadPitchSlider.Value:0}  Y {HeadYawSlider.Value:0}  R {HeadRollSlider.Value:0}";
    }

    private void ApplyModeUi()
    {
        var mode = (ModeCombo.SelectedItem as string) ?? "tcp";
        var file = string.Equals(mode, "file", StringComparison.OrdinalIgnoreCase);
        OutputPathBox.IsEnabled = file;
        OutputPathBox.Opacity = file ? 1.0 : 0.55;
    }

    private void OnSessionLog(string level, string message) => Dispatcher.BeginInvoke(new Action(() => AppendLog(level, message)));

    private void ConnectBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLooping) StopLoopInternal();
        _session?.Dispose();
        _session = null;

        var host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            AppendLog("ERROR", "Invalid port.");
            return;
        }
        if (!ulong.TryParse(StartSeqBox.Text.Trim(), out var startSeq))
        {
            AppendLog("ERROR", "Invalid start sequence.");
            return;
        }
        var mode = (ModeCombo.SelectedItem as string) ?? "tcp";
        var outPath = string.IsNullOrWhiteSpace(OutputPathBox.Text) ? "outputs/frames.jsonl" : OutputPathBox.Text.Trim();
        _session = new SenderSession(new SenderSession.SessionOptions(mode, host, port, outPath, CharacterIdBox.Text.Trim(), startSeq), OnSessionLog);
        if (_session.Connect()) SetConnectedState(true, $"Connected {host}:{port}");
        else
        {
            _session.Dispose();
            _session = null;
            SetConnectedState(false, "Connect failed");
        }
    }

    private void DisconnectBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLooping) StopLoopInternal();
        _session?.Disconnect();
        _session?.Dispose();
        _session = null;
        SetConnectedState(false, "Disconnected");
    }

    private void SetConnectedState(bool connected, string message = "")
    {
        _isConnected = connected;
        ConnStatus.Text = string.IsNullOrEmpty(message) ? (connected ? "Connected" : "Disconnected") : message;
        ConnStatus.Foreground = connected ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkRed;
        ConnectBtn.IsEnabled = !connected && !_isLooping;
        DisconnectBtn.IsEnabled = connected && !_isLooping;
    }

    private void SetLoopingState(bool looping)
    {
        _isLooping = looping;
        StartLoopBtn.IsEnabled = !looping;
        StopLoopBtn.IsEnabled = looping;
        SendOnceBtn.IsEnabled = !looping;
        SendPresetBtn.IsEnabled = !looping;
        ConnectBtn.IsEnabled = !looping && !_isConnected;
        DisconnectBtn.IsEnabled = !looping && _isConnected;
    }

    private MapperInput BuildInputFromForm() => new()
    {
        Text = TextBoxLine.Text,
        EmotionLabel = (EmotionCombo.SelectedItem as string) ?? "neutral",
        PhonemeHint = (PhonemeCombo.SelectedItem as string) ?? "rest",
        Rms = RmsSlider.Value,
        EmotionConfidence = ConfSlider.Value,
        HeadPosePitch = HeadPitchSlider.Value,
        HeadPoseYaw = HeadYawSlider.Value,
        HeadPoseRoll = HeadRollSlider.Value,
    };

    private void ApplyPresetToForm(FramePreset preset)
    {
        TextBoxLine.Text = preset.Text;
        SelectComboItem(EmotionCombo, preset.Emotion);
        SelectComboItem(PhonemeCombo, preset.Phoneme);
        RmsSlider.Value = preset.Rms;
        ConfSlider.Value = preset.Confidence;
        HeadPitchSlider.Value = preset.HeadPitch;
        HeadYawSlider.Value = preset.HeadYaw;
        HeadRollSlider.Value = preset.HeadRoll;
    }

    private static void SelectComboItem(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is string s && s == value) { box.SelectedIndex = i; return; }
    }

    private void PresetList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is not FramePreset p) { PresetSummary.Text = ""; return; }
        ApplyPresetToForm(p);
        PresetSummary.Text = $"{p.Emotion} / {p.Phoneme}  rms={p.Rms:0.00}  conf={p.Confidence:0.00}\n{p.Text}";
    }

    private void PresetList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PresetList.SelectedItem is FramePreset p) { ApplyPresetToForm(p); SendOnceInternal(); }
    }

    private void SendPresetBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not FramePreset p) { AppendLog("WARN", "No preset selected."); return; }
        ApplyPresetToForm(p);
        SendOnceInternal();
    }

    private void SendOnceInternal()
    {
        var mode = (ModeCombo.SelectedItem as string) ?? "tcp";
        if (_session == null || !_session.IsConnected)
        {
            if (mode == "tcp") { AppendLog("ERROR", "Not connected. Click Connect first."); return; }
            ConnectBtn_OnClick(this, new RoutedEventArgs());
            if (_session == null || !_session.IsConnected) return;
        }
        HandleSendResult(_session.SendFrame(BuildInputFromForm()));
    }

    private void SendOnceBtn_OnClick(object sender, RoutedEventArgs e) => SendOnceInternal();

    private void StartLoopBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLooping) return;
        if (_session == null || !_session.IsConnected)
        {
            AppendLog("ERROR", "Connect before starting loop.");
            return;
        }
        if (!int.TryParse(CountBox.Text.Trim(), out var count) || count < 0)
        {
            AppendLog("ERROR", "Invalid count.");
            return;
        }
        _loopTarget = count;
        _loopSent = 0;
        _loopTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (int)(1000.0 / FpsSlider.Value)));
        SetLoopingState(true);
        _loopTimer.Start();
    }

    private void StopLoopBtn_OnClick(object sender, RoutedEventArgs e) => StopLoopInternal();

    private void StopLoopInternal()
    {
        if (!_isLooping) return;
        _loopTimer.Stop();
        SetLoopingState(false);
        AppendLog("INFO", $"Loop stopped. Sent {_loopSent} frames.");
    }

    private void LoopTimer_OnTick(object? sender, EventArgs e)
    {
        if (_session == null || !_session.IsConnected)
        {
            StopLoopInternal();
            SetConnectedState(false, "Connection lost");
            return;
        }
        var result = _session.SendFrame(BuildInputFromForm());
        HandleSendResult(result);
        if (!result.Success)
        {
            StopLoopInternal();
            return;
        }
        _loopSent++;
        if (_loopTarget > 0 && _loopSent >= _loopTarget) StopLoopInternal();
    }

    private void HandleSendResult(SenderSession.SendResult result)
    {
        if (!result.Success)
        {
            SbError.Text = "ERR: " + result.ErrorMessage;
            AppendLog("ERROR", result.ErrorMessage);
            return;
        }
        LastSendLabel.Text = "Last send: " + DateTime.Now.ToString("HH:mm:ss.fff");
        LastSeqLabel.Text = "Last seq: " + result.Seq;
        SbSeq.Text = "seq: " + result.Seq;
        SbBytes.Text = "bytes: " + result.PayloadBytes;
        SbError.Text = "";
        try
        {
            using var doc = JsonDocument.Parse(result.JsonPayload);
            SbEmotion.Text = "emotion: " + doc.RootElement.GetProperty("emotion").GetProperty("label").GetString();
            SbPhoneme.Text = "phoneme: " + doc.RootElement.GetProperty("audio").GetProperty("phoneme_hint").GetString();
        }
        catch { /* status only */ }
    }

    private void TestTcpBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) { AppendLog("ERROR", "Invalid port."); return; }
        if (SenderSession.ProbeTcp(HostBox.Text.Trim(), port, out var err)) AppendLog("INFO", "TCP probe passed.");
        else AppendLog("ERROR", "TCP probe failed: " + err);
    }

    private void SendSampleBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session == null || !_session.IsConnected) { AppendLog("ERROR", "Connect before sending sample_frame.json."); return; }
        try
        {
            HandleSendResult(_session.SendRawJson(File.ReadAllText(RepoPaths.SampleFrameJson)));
        }
        catch (Exception ex) { AppendLog("ERROR", ex.Message); }
    }

    private void ClearLogBtn_OnClick(object sender, RoutedEventArgs e)
    {
        AppLogBox.Clear(); JsonLogBox.Clear(); ErrorLogBox.Clear(); _rawJsonLines.Clear();
    }

    private void SaveLogBtn_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RepoPaths.LogsDir);
        var prefix = Path.Combine(RepoPaths.LogsDir, "sender_gui_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        File.WriteAllText(prefix + "_app.log", AppLogBox.Text);
        File.WriteAllText(prefix + "_errors.log", ErrorLogBox.Text);
        File.WriteAllLines(prefix + "_frames.jsonl", _rawJsonLines);
        AppendLog("INFO", "Logs saved: " + prefix);
    }

    private void CopyJsonBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_session?.LastJson)) { AppendLog("WARN", "No latest JSON."); return; }
        Clipboard.SetText(_session.LastJson);
        AppendLog("INFO", "Latest JSON copied.");
    }

    private void ApplyAiRuntimeDefaults()
    {
        var p = AiRuntimePaths.Create();
        AiPythonExeBox.Text = p.DefaultPythonExe;
        AiIndexTtsRepoBox.Text = p.DefaultIndexTtsRepo;
        AiIndexTtsModelDirBox.Text = p.DefaultModelDir;
        AiIndexTtsConfigBox.Text = p.DefaultIndexTtsConfig;
        AiIndexTtsInferScriptBox.Text = "";
        AiOutDirBox.Text = p.DefaultOutDir;
        AiMorphMapBox.Text = p.DefaultMorphMap;
        AiSkeletonTreeBox.Text = p.DefaultSkeletonTree;
        AiFpsBox.Text = "30";
        AiEmotionStrengthBox.Text = "0.7";
        AiBlinkEnabledCheckBox.IsChecked = true;
        AiUseTimestampCheckBox.IsChecked = true;
        AiOfflineModeCheckBox.IsChecked = true;
        AiUseGeneratedLocalConfigCheckBox.IsChecked = false;
        AiEffectiveConfigLabel.Text = "-";
        AppendAiLog("INFO", "AI Runtime defaults applied.");
        AppendAiLog("INFO", "Default model dir: " + p.DefaultModelDir);
        AppendAiLog("INFO", "Default config: " + p.DefaultIndexTtsConfig);
        var indexTtsUvPy = Path.Combine(p.RepoRoot, "third_party", "index-tts", ".venv", "Scripts", "python.exe");
        if (!File.Exists(indexTtsUvPy))
            AppendAiLog("WARN", "IndexTTS2 uv environment not found. Run uv sync in third_party/index-tts first.");
        UpdateAiRuntimeEnvStatus("Not checked");
    }

    private AiRuntimeOptions BuildAiRuntimeOptionsFromUi()
    {
        int.TryParse(AiFpsBox.Text.Trim(), out var fps);
        if (fps <= 0) fps = 30;
        double.TryParse(AiEmotionStrengthBox.Text.Trim(), out var strength);
        if (strength <= 0) strength = 0.7;
        double? target = null;
        if (double.TryParse(AiTargetDurationBox.Text.Trim(), out var td) && td > 0) target = td;
        var basicBackend = GetAiBasicBackend();
        var rawBackend = (AiTtsBackendCombo.SelectedItem as string) ?? "dryrun";
        var backend = basicBackend switch
        {
            "dryrun" => "dryrun",
            "real IndexTTS2" => rawBackend == "dryrun" ? "auto" : rawBackend,
            "use existing WAV" => "dryrun",
            _ => rawBackend,
        };
        return new AiRuntimeOptions
        {
            Text = AiTextBox.Text,
            GuiMode = basicBackend switch
            {
                "real IndexTTS2" => "real_indextts2",
                "use existing WAV" => "existing_wav",
                _ => "dryrun",
            },
            Emotion = (AiEmotionCombo.SelectedItem as string) ?? "happy",
            TtsBackend = backend,
            PythonExe = AiPythonExeBox.Text.Trim(),
            IndexTtsRepo = AiIndexTtsRepoBox.Text.Trim(),
            IndexTtsModelDir = AiIndexTtsModelDirBox.Text.Trim(),
            IndexTtsConfig = AiIndexTtsConfigBox.Text.Trim(),
            IndexTtsInferScript = AiIndexTtsInferScriptBox.Text.Trim(),
            SpeakerWav = AiSpeakerWavBox.Text.Trim(),
            ExistingWav = AiExistingWavBox.Text.Trim(),
            EmotionPrompt = AiEmotionPromptBox.Text.Trim(),
            TargetDurationSec = target,
            OutDir = AiOutDirBox.Text.Trim(),
            Fps = fps,
            CharacterId = "yyb_miku",
            MorphMap = AiMorphMapBox.Text.Trim(),
            SkeletonTree = AiSkeletonTreeBox.Text.Trim(),
            EmotionStrength = strength,
            BlinkEnabled = AiBlinkEnabledCheckBox.IsChecked == true,
            UseTimestamp = AiUseTimestampCheckBox.IsChecked == true,
            DryRun = basicBackend == "dryrun",
            SkipTts = basicBackend == "use existing WAV",
            UseFp16 = AiUseFp16CheckBox.IsChecked == true,
            UseCudaKernel = AiUseCudaKernelCheckBox.IsChecked == true,
            UseDeepSpeed = AiUseDeepSpeedCheckBox.IsChecked == true,
            UseRandom = AiUseRandomCheckBox.IsChecked == true,
            FallbackToDryrun = AiFallbackToDryrunCheckBox.IsChecked == true,
            OfflineMode = AiOfflineModeCheckBox.IsChecked != false,
            AllowOnline = AiOfflineModeCheckBox.IsChecked == false,
            UseGeneratedLocalConfig = AiUseGeneratedLocalConfigCheckBox.IsChecked == true,
        };
    }

    private bool ValidateAiOptions(AiRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Text)) { AppendAiLog("ERROR", "Text is empty."); return false; }
        if (string.IsNullOrWhiteSpace(options.OutDir)) { AppendAiLog("ERROR", "OutDir is empty."); return false; }
        var basicBackend = GetAiBasicBackend();
        if (basicBackend == "real IndexTTS2")
        {
            if (string.IsNullOrWhiteSpace(options.SpeakerWav) || !File.Exists(options.SpeakerWav))
            {
                AppendAiLog("ERROR", "Real IndexTTS2 requires Speaker WAV / spk_audio_prompt. Please choose a reference voice wav first.");
                MessageBox.Show(this, "Real IndexTTS2 requires Speaker WAV / spk_audio_prompt. Please choose a reference voice wav first.", "AI Runtime", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!Directory.Exists(options.IndexTtsModelDir ?? ""))
            {
                AppendAiLog("ERROR", "Model dir does not exist: " + options.IndexTtsModelDir);
                return false;
            }
            if (!File.Exists(options.IndexTtsConfig ?? ""))
            {
                AppendAiLog("ERROR", "config.yaml not found. Model download may be incomplete: " + options.IndexTtsConfig);
                return false;
            }
            if (!string.IsNullOrWhiteSpace(options.IndexTtsRepo) && !Directory.Exists(options.IndexTtsRepo))
            {
                AppendAiLog("ERROR", "IndexTTS repo directory does not exist: " + options.IndexTtsRepo);
                return false;
            }
            if (string.IsNullOrWhiteSpace(options.IndexTtsRepo))
                AppendAiLog("WARN", "Repo is empty. The selected Python must already have indextts installed.");
        }
        if (basicBackend == "use existing WAV" && (string.IsNullOrWhiteSpace(options.ExistingWav) || !File.Exists(options.ExistingWav)))
        {
            AppendAiLog("ERROR", "Use existing WAV mode requires an existing WAV file.");
            return false;
        }
        return true;
    }

    private async void AiGenerateBtn_OnClick(object sender, RoutedEventArgs e) => await StartAiGenerateAsync(playAfter: false);
    private async void AiGenerateAndPlayBtn_OnClick(object sender, RoutedEventArgs e) => await StartAiGenerateAsync(playAfter: true);

    private Task StartAiGenerateAsync(bool playAfter)
    {
        var options = BuildAiRuntimeOptionsFromUi();
        if (!ValidateAiOptions(options)) return Task.CompletedTask;
        var basicBackend = GetAiBasicBackend();
        _aiGenerateCts?.Cancel();
        _aiGenerateCts = new CancellationTokenSource();
        var token = _aiGenerateCts.Token;
        SetAiGeneratingState(true);
        StartAiProgress("starting", "Starting...", indeterminate: true, 5);
        AppendAiLog("INFO", "Selected Python: " + options.PythonExe);
        AppendAiLog("INFO", "IndexTTS Repo: " + options.IndexTtsRepo);
        AppendAiLog("INFO", "Model Dir: " + options.IndexTtsModelDir);
        AppendAiLog("INFO", "Config: " + options.IndexTtsConfig);
        AppendAiLog("INFO", "Speaker WAV: " + options.SpeakerWav);
        AppendAiLog("INFO", "Backend: " + options.TtsBackend);
        if (options.OfflineMode)
            AppendAiLog("INFO", "Offline env: HF_HUB_OFFLINE=1, TRANSFORMERS_OFFLINE=1, HF_HUB_DISABLE_XET=1");
        else
            AppendAiLog("WARN", "Online model loading may access Hugging Face and can hang or timeout.");
        AppendAiLog("INFO", "Resolved config path: " + Path.Combine(options.OutDir, "ai_runtime.resolved.json"));
        AppendAiLog("INFO", "Resolved config encoding: UTF-8 no BOM");
        AiPythonStatusLabel.Text = options.PythonExe ?? "-";

        _ = Task.Run(async () =>
        {
            try
            {
                if (basicBackend == "real IndexTTS2")
                {
                    EnqueueAiLog("INFO", "Running Check Setup with current Python before real IndexTTS2 generation...");
                    var setup = await AiRuntimeSetupCheckService.CheckAsync(options, basicBackend, AppendAiProcessOutput, token).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _lastAiSetupCheck = setup;
                        ApplyAiSetupCheckResult(options, basicBackend, setup);
                    });
                    if (!setup.OverallOk)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            SetAiProgress("check_setup", "Setup Error", null, indeterminate: false, "Failed", Brushes.DarkRed);
                            StopAiProgressTimer();
                            SetAiGeneratingState(false);
                        });
                        return;
                    }
                }
                EnqueueAiLog("INFO", "Generating AI frames...");
                var result = await _aiProcess.GenerateAsync(options, AppendAiProcessOutput, token).ConfigureAwait(false);
                var uiTask = await Dispatcher.InvokeAsync(() => HandleAiGenerateResultAsync(result, playAfter));
                await uiTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAiProgress("cancelled", "Cancelled", null, indeterminate: false, "Cancelled", Brushes.DarkOrange);
                    AppendAiLog("WARN", "AI generation cancelled by user.");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAiProgress(_aiCurrentStage, "Failed", null, indeterminate: false, "Failed", Brushes.DarkRed);
                    AiLastErrorLabel.Text = ex.Message;
                    AppendAiLog("ERROR", ex.Message);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StopAiProgressTimer();
                    SetAiGeneratingState(false);
                });
            }
        }, token);
        return Task.CompletedTask;
    }

    private async Task HandleAiGenerateResultAsync(AiRuntimeResult result, bool playAfter)
    {
        _lastAiResult = result;
        _lastAiFramesPath = _lastAiResult.FramesPath;
        _lastAiManifestPath = _lastAiResult.ManifestPath;
        AiLastWavLabel.Text = _lastAiResult.WavPath ?? "-";
        AiLastFramesLabel.Text = _lastAiFramesPath ?? "-";
        AiLastManifestLabel.Text = _lastAiManifestPath ?? "-";
        if (_lastAiResult.Manifest != null) DisplayAiManifest(_lastAiResult.Manifest);
        else if (!string.IsNullOrEmpty(_lastAiResult.RawManifestText)) AiManifestBox.Text = _lastAiResult.RawManifestText;
        else if (!string.IsNullOrEmpty(_lastAiManifestPath) && File.Exists(_lastAiManifestPath)) AiManifestBox.Text = await File.ReadAllTextAsync(_lastAiManifestPath);
        if (!string.IsNullOrEmpty(_lastAiResult.ManifestParseError))
        {
            if (!string.IsNullOrEmpty(_lastAiResult.FailedManifestPath))
                AppendAiLog("WARN", "Failed to parse manifest_failed.json, showing raw file content instead.");
            AppendAiLog("WARN", "Manifest parse issue: " + _lastAiResult.ManifestParseError);
        }
        AppendAiLog(_lastAiResult.Success ? "INFO" : "ERROR", $"Generate finished exit={_lastAiResult.ExitCode} elapsed={_lastAiResult.Elapsed.TotalSeconds:0.00}s");
        if (!_lastAiResult.Success)
        {
            SetAiProgress(_aiCurrentStage, "Failed", null, indeterminate: false, "Failed", Brushes.DarkRed);
            AiLastErrorLabel.Text = _lastAiResult.ErrorMessage ?? "Generate failed";
            AppendAiLog("ERROR", _lastAiResult.ErrorMessage ?? "Generate failed");
            return;
        }
        SetAiProgress("done", "Done", 100, indeterminate: false, "Success", Brushes.DarkGreen);
        if (playAfter) await PlayAiFramesAsync(_lastAiFramesPath);
    }

    private async void AiPlayExistingFramesBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastAiFramesPath) && File.Exists(_lastAiFramesPath))
        {
            await PlayAiFramesAsync(_lastAiFramesPath);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "JSONL frames|*.jsonl|All files|*.*", InitialDirectory = AiOutDirBox.Text };
        if (dlg.ShowDialog(this) == true) await PlayAiFramesAsync(dlg.FileName);
    }

    private async Task PlayAiFramesAsync(string? framesPath)
    {
        if (string.IsNullOrWhiteSpace(framesPath) || !File.Exists(framesPath)) { AppendAiLog("ERROR", "frames.jsonl not found."); return; }
        if (_session == null || !_session.IsConnected) { AppendAiLog("ERROR", "Connect to UE first using the existing Connection section."); return; }
        _aiPlaybackCts?.Cancel();
        _aiPlaybackCts = new CancellationTokenSource();
        SetAiPlaybackState(true);
        StartAiProgress("playback", "Starting playback...", indeterminate: false, 0);
        try
        {
            AppendAiLog("PLAYBACK", "Playing frames to UE: " + framesPath);
            var fps = double.TryParse(AiFpsBox.Text, out var v) ? v : 30.0;
            await _aiPlayback.PlayAsync(_session, framesPath, AiUseTimestampCheckBox.IsChecked == true, fps, UpdateAiPlaybackProgress, _aiPlaybackCts.Token);
            SetAiProgress("playback", "Playback done", 100, indeterminate: false, "Success", Brushes.DarkGreen);
            AppendAiLog("PLAYBACK", "Playback complete.");
        }
        catch (OperationCanceledException)
        {
            SetAiProgress("playback", "Playback cancelled", null, indeterminate: false, "Cancelled", Brushes.DarkOrange);
            AppendAiLog("WARN", "AI playback stopped.");
        }
        catch (Exception ex)
        {
            SetAiProgress("playback", "Playback failed", null, indeterminate: false, "Failed", Brushes.DarkRed);
            AiLastErrorLabel.Text = ex.Message;
            AppendAiLog("ERROR", ex.Message);
        }
        finally
        {
            StopAiProgressTimer();
            SetAiPlaybackState(false);
        }
    }

    private void AiStopPlaybackBtn_OnClick(object sender, RoutedEventArgs e)
    {
        AppendAiLog("WARN", "Cancelling...");
        _aiGenerateCts?.Cancel();
        _aiPlaybackCts?.Cancel();
        _aiSetupCts?.Cancel();
        _aiProcess.TryStopRunningProcessTree(AppendAiProcessOutput);
        AppendAiLog("WARN", "Stop requested.");
    }

    private void AiOpenOutputFolderBtn_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AiOutDirBox.Text);
            Process.Start(new ProcessStartInfo { FileName = AiOutDirBox.Text, UseShellExecute = true });
        }
        catch (Exception ex) { AppendAiLog("ERROR", ex.Message); }
    }

    private void AiCopyCommandBtn_OnClick(object sender, RoutedEventArgs e)
    {
        var cmd = BuildAiRuntimeCommandPreview(BuildAiRuntimeOptionsFromUi());
        Clipboard.SetText(cmd);
        AppendAiLog("INFO", "AI command copied.");
    }

    private void AiClearLogBtn_OnClick(object sender, RoutedEventArgs e)
    {
        AiLogBox.Clear(); AiProcessOutputBox.Clear(); AiManifestBox.Clear();
    }

    private async void AiCheckModelBtn_OnClick(object sender, RoutedEventArgs e)
    {
        _aiSetupCts?.Cancel();
        _aiSetupCts = new CancellationTokenSource();
        var options = BuildAiRuntimeOptionsFromUi();
        var basicBackend = GetAiBasicBackend();
        SetAiGeneratingState(true);
        StartAiProgress("check_setup", "Checking setup...", indeterminate: true, 5);
        _ = Task.Run(async () =>
        {
            try
            {
                EnqueueAiLog("INFO", "Running Check Setup...");
                var result = await AiRuntimeSetupCheckService.CheckAsync(options, basicBackend, AppendAiProcessOutput, _aiSetupCts.Token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastAiSetupCheck = result;
                    ApplyAiSetupCheckResult(options, basicBackend, result);
                    SetAiProgress("check_setup", result.OverallOk ? "Setup OK" : "Setup Error", result.OverallOk ? 100 : null, indeterminate: false, result.OverallOk ? "Success" : "Failed", result.OverallOk ? Brushes.DarkGreen : Brushes.DarkRed);
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAiProgress("check_setup", "Setup check cancelled", null, indeterminate: false, "Cancelled", Brushes.DarkOrange);
                    AppendAiLog("WARN", "Check Setup cancelled.");
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StopAiProgressTimer();
                    SetAiGeneratingState(false);
                });
            }
        }, _aiSetupCts.Token);
    }

    private void AiBrowsePythonBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFileInto(AiPythonExeBox, "Python|python.exe|All files|*.*");
    private void AiBrowseSpeakerWavBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFileInto(AiSpeakerWavBox, "WAV|*.wav|All files|*.*");
    private void AiBrowseExistingWavBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFileInto(AiExistingWavBox, "WAV|*.wav|All files|*.*");
    private void AiBrowseIndexTtsRepoBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFolderInto(AiIndexTtsRepoBox);
    private void AiBrowseIndexTtsModelDirBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFolderInto(AiIndexTtsModelDirBox);
    private void AiBrowseIndexTtsConfigBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFileInto(AiIndexTtsConfigBox, "YAML|*.yaml;*.yml|All files|*.*");
    private void AiBrowseIndexTtsInferScriptBtn_OnClick(object sender, RoutedEventArgs e) => BrowseFileInto(AiIndexTtsInferScriptBox, "Python|*.py|All files|*.*");
    private void AiUseIndexTtsUvEnvBtn_OnClick(object sender, RoutedEventArgs e)
    {
        var p = AiRuntimePaths.Create();
        AiIndexTtsRepoBox.Text = Path.Combine(p.RepoRoot, "third_party", "index-tts");
        AiPythonExeBox.Text = Path.Combine(AiIndexTtsRepoBox.Text, ".venv", "Scripts", "python.exe");
        UpdateAiRuntimeEnvStatus("Not checked");
        AppendAiLog("INFO", "Selected IndexTTS2 uv Python: " + AiPythonExeBox.Text);
    }

    private void AiPatchLocalPathsBtn_OnClick(object sender, RoutedEventArgs e)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "patch_indextts2_local_paths.ps1");
        if (!File.Exists(script))
        {
            AppendAiLog("ERROR", "Patch script not found: " + script);
            return;
        }
        var indexRepo = AiIndexTtsRepoBox.Text.Trim();
        SetAiGeneratingState(true);
        StartAiProgress("patch_local_paths", "Patching IndexTTS2 local paths...", indeterminate: true, 5);
        AppendAiLog("INFO", "Running local path patch: " + script);
        _ = Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteForCommand(script) + " -IndexTtsRepo " + QuoteForCommand(indexRepo),
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start patch script.");
                var outTask = PumpProcessLinesAsync(process.StandardOutput, "STDOUT", _aiSetupCts?.Token ?? CancellationToken.None);
                var errTask = PumpProcessLinesAsync(process.StandardError, "STDERR", _aiSetupCts?.Token ?? CancellationToken.None);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAiProgress("patch_local_paths", process.ExitCode == 0 ? "Patch complete" : "Patch failed", process.ExitCode == 0 ? 100 : null, false, process.ExitCode == 0 ? "Success" : "Failed", process.ExitCode == 0 ? Brushes.DarkGreen : Brushes.DarkRed);
                    AppendAiLog(process.ExitCode == 0 ? "INFO" : "ERROR", "Patch Local Paths exit=" + process.ExitCode);
                    SetAiGeneratingState(false);
                    StopAiProgressTimer();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAiProgress("patch_local_paths", "Patch failed", null, false, "Failed", Brushes.DarkRed);
                    AppendAiLog("ERROR", ex.Message);
                    SetAiGeneratingState(false);
                    StopAiProgressTimer();
                });
            }
        });
    }

    private async Task PumpProcessLinesAsync(StreamReader reader, string stream, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            AppendAiProcessOutput(stream, line);
        }
    }

    private void AiOpenIndexTtsRepoBtn_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(AiIndexTtsRepoBox.Text))
            {
                AppendAiLog("ERROR", "IndexTTS repo directory does not exist: " + AiIndexTtsRepoBox.Text);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = AiIndexTtsRepoBox.Text, UseShellExecute = true });
        }
        catch (Exception ex) { AppendAiLog("ERROR", ex.Message); }
    }

    private void SetAiGeneratingState(bool active)
    {
        _isAiGenerating = active;
        AiGenerateBtn.IsEnabled = !active;
        AiGenerateAndPlayBtn.IsEnabled = !active && !_isAiPlaying;
        AiCheckModelBtn.IsEnabled = !active;
    }

    private async Task<AiSetupCheckResult> RunAiSetupCheckAsync(AiRuntimeOptions options, CancellationToken ct)
    {
        AppendAiLog("INFO", "Running Check Setup...");
        SetAiProgress("check_setup", "Checking paths...", 10, indeterminate: false, "Running", Brushes.SteelBlue);
        var basicBackend = GetAiBasicBackend();
        var result = await AiRuntimeSetupCheckService.CheckAsync(options, basicBackend, AppendAiProcessOutput, ct);
        ApplyAiSetupCheckResult(options, basicBackend, result);
        return result;
    }

    private void ApplyAiSetupCheckResult(AiRuntimeOptions options, string basicBackend, AiSetupCheckResult result)
    {
        AiModelCheckLabel.Text = result.Summary;
        AiModelCheckLabel.Foreground = result.OverallOk
            ? (result.Warnings.Count > 0 ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.DarkGreen)
            : System.Windows.Media.Brushes.DarkRed;
        foreach (var err in result.Errors) AppendAiLog("ERROR", err);
        foreach (var warn in result.Warnings) AppendAiLog("WARN", warn);
        AppendAiLog(result.OverallOk ? "INFO" : "ERROR", result.Summary);
        AiImportStatusLabel.Text = basicBackend switch
        {
            "dryrun" => "Skipped for dryrun",
            "use existing WAV" => "Skipped for existing WAV",
            _ => result.PythonImportOk ? "OK" : "Failed",
        };
        AiPythonStatusLabel.Text = options.PythonExe ?? "-";
        UpdateAiRuntimeEnvStatus(AiImportStatusLabel.Text);
    }

    private string GetAiBasicBackend() => (AiBasicBackendCombo.SelectedItem as string) ?? "dryrun";

    private void AiBasicBackendCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiSpeakerWavBox == null) return;
        ApplyAiBasicBackendUi();
    }

    private void ApplyAiBasicBackendUi()
    {
        var mode = GetAiBasicBackend();
        var real = mode == "real IndexTTS2";
        var existing = mode == "use existing WAV";
        AiSpeakerWavLabel.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
        AiSpeakerWavBox.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
        AiBrowseSpeakerWavBtn.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
        AiExistingWavLabel.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        AiExistingWavBox.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        AiBrowseExistingWavBtn.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "dryrun") AiTtsBackendCombo.SelectedItem = "dryrun";
        if (mode == "real IndexTTS2" && ((AiTtsBackendCombo.SelectedItem as string) == "dryrun" || AiTtsBackendCombo.SelectedItem == null))
            AiTtsBackendCombo.SelectedItem = "auto";
        UpdateAiRuntimeEnvStatus("Not checked");
    }

    private void UpdateAiRuntimeEnvStatus(string importStatus)
    {
        if (AiRuntimeEnvStatusText == null) return;
        var python = AiPythonExeBox?.Text?.Trim() ?? "";
        var repo = AiIndexTtsRepoBox?.Text?.Trim() ?? "";
        var uvPy = string.IsNullOrWhiteSpace(repo) ? "" : Path.Combine(repo, ".venv", "Scripts", "python.exe");
        var pythonExists = IsCommandOrFileAvailable(python);
        var repoExists = !string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo);
        var venvExists = !string.IsNullOrWhiteSpace(uvPy) && File.Exists(uvPy);
        AiRuntimeEnvStatusText.Text = $"Python exists: {(pythonExists ? "yes" : "no")} | Repo exists: {(repoExists ? "yes" : "no")} | venv exists: {(venvExists ? "yes" : "no")} | import indextts: {importStatus}";
    }

    private static bool IsCommandOrFileAvailable(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (File.Exists(value)) return true;
        return value.Equals("python", StringComparison.OrdinalIgnoreCase) || value.Equals("py -3", StringComparison.OrdinalIgnoreCase);
    }

    private void SetAiPlaybackState(bool active)
    {
        _isAiPlaying = active;
        AiPlayExistingFramesBtn.IsEnabled = !active && !_isAiGenerating;
        AiGenerateAndPlayBtn.IsEnabled = !active && !_isAiGenerating;
    }

    private void AppendAiLog(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [AI][{level}] {message}";
        _pendingAiLog.Enqueue(line);
        EnsureAiFlushTimer();
        if (level is "ERROR" or "WARN") AiLastErrorLabel.Text = message;
    }

    private void EnqueueAiLog(string level, string message)
    {
        _pendingAiLog.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [AI][{level}] {message}");
        EnsureAiFlushTimer();
    }

    private void AppendAiProcessOutput(string stream, string line)
    {
        _pendingAiProcess.Enqueue($"[AI][PROCESS][{stream}] {line}");
        _pendingAiParsed.Enqueue((stream, line));
        EnsureAiFlushTimer();
    }

    private void ParseSetupLine(string line)
    {
        if (!line.StartsWith("Step ", StringComparison.OrdinalIgnoreCase))
            return;
        if (line.Contains("Path check", StringComparison.OrdinalIgnoreCase))
            SetAiProgress("check_paths", "Checking paths...", 10, false, "Running", Brushes.SteelBlue);
        else if (line.Contains("Python executable", StringComparison.OrdinalIgnoreCase))
            SetAiProgress("check_python", "Checking Python executable...", 35, false, "Running", Brushes.SteelBlue);
        else if (line.Contains("IndexTTS import", StringComparison.OrdinalIgnoreCase))
            SetAiProgress("check_import", "Checking indextts import...", null, true, "Waiting / import", Brushes.DarkOrange);
        else if (line.Contains("Dependency quick", StringComparison.OrdinalIgnoreCase))
            SetAiProgress("check_dependency", "Checking OffloadedCache dependency...", 75, false, "Running", Brushes.SteelBlue);
    }

    private void DisplayAiManifest(AiRuntimeManifest manifest)
    {
        AiFrameCountLabel.Text = manifest.FrameCount.ToString();
        AiDurationLabel.Text = manifest.DurationSec.ToString("0.000") + " sec";
        AiTtsResolvedLabel.Text = manifest.TtsBackendResolved;
        AiEffectiveConfigLabel.Text = string.IsNullOrWhiteSpace(manifest.EffectiveConfig) ? "-" : manifest.EffectiveConfig;
        AiManifestBox.Text = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private void UpdateAiPlaybackProgress(int current, int total, ulong? sequenceId, double? timeSec)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var pct = total <= 0 ? 0 : Math.Clamp(current * 100.0 / total, 0, 100);
            SetAiProgress("playback", $"Playing frame {current} / {total}", pct, indeterminate: false, "Running", Brushes.SteelBlue);
            AiPlaybackProgressLabel.Text = $"{current}/{total} seq={sequenceId?.ToString() ?? "-"} time={timeSec:0.000}";
        }));
    }

    private void StartAiProgress(string stage, string text, bool indeterminate, double value)
    {
        _aiOperationStartedAt = DateTime.Now;
        _lastAiStillLogAt = DateTime.MinValue;
        SetAiProgress(stage, text, value, indeterminate, "Running", Brushes.SteelBlue);
        AiHeartbeatText.Text = "Last update: " + DateTime.Now.ToString("HH:mm:ss");
        AiUiHeartbeatText.Text = "UI heartbeat: " + DateTime.Now.ToString("HH:mm:ss");
        _aiProgressTimer.Start();
    }

    private void StopAiProgressTimer()
    {
        _aiProgressTimer.Stop();
        AiUiHeartbeatText.Text = "-";
    }

    private void AiProgressTimer_OnTick(object? sender, EventArgs e)
    {
        if (_aiOperationStartedAt == default) return;
        var elapsed = DateTime.Now - _aiOperationStartedAt;
        AiElapsedText.Text = "Elapsed: " + elapsed.ToString(@"hh\:mm\:ss");
        if (_isAiGenerating || _isAiPlaying)
            AiUiHeartbeatText.Text = "UI heartbeat: " + DateTime.Now.ToString("HH:mm:ss");
        else
            AiUiHeartbeatText.Text = "-";
        if (_isAiGenerating && (DateTime.Now - _lastAiStillLogAt).TotalSeconds >= 2)
        {
            AppendAiLog("INFO", $"Still running stage={_aiCurrentStage} elapsed={elapsed.TotalSeconds:0} sec");
            _lastAiStillLogAt = DateTime.Now;
        }
    }

    private void SetAiProgress(string stage, string text, double? value, bool indeterminate, string status, Brush brush)
    {
        _aiCurrentStage = stage;
        AiStageText.Text = "Stage: " + stage;
        AiProgressText.Text = text;
        AiProgressBar.IsIndeterminate = indeterminate;
        if (value.HasValue)
            AiProgressBar.Value = Math.Clamp(value.Value, 0, 100);
        AiRunStatusLabel.Text = status;
        AiRunStatusLabel.Foreground = brush;
        AiHeartbeatText.Text = "Last update: " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void ParseAiRuntimeLine(string stream, string line)
    {
        if (!line.Contains("[AI_RUNTIME]", StringComparison.OrdinalIgnoreCase))
            return;
        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            SetAiProgress(_aiCurrentStage, "Failed", null, indeterminate: false, "Failed", Brushes.DarkRed);
            AppendAiLog("ERROR", line);
            return;
        }
        if (line.Contains("[CANCELLED]", StringComparison.OrdinalIgnoreCase))
        {
            SetAiProgress("cancelled", "Cancelled", null, indeterminate: false, "Cancelled", Brushes.DarkOrange);
            AppendAiLog("WARN", line);
            return;
        }
        if (line.Contains("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase))
        {
            var stage = ExtractRuntimeStage(line) ?? _aiCurrentStage;
            _aiCurrentStage = stage;
            AiStageText.Text = "Stage: " + stage;
            AiHeartbeatText.Text = "Last heartbeat: " + DateTime.Now.ToString("HH:mm:ss");
            return;
        }
        if (line.Contains("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            SetAiProgress("done", "Done", 100, indeterminate: false, "Success", Brushes.DarkGreen);
            return;
        }
        if (line.Contains("[STAGE]", StringComparison.OrdinalIgnoreCase) || line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase))
        {
            var stage = ExtractRuntimeStage(line);
            if (stage != null)
                ApplyStageProgress(stage, line);
        }
    }

    private void ApplyStageProgress(string stage, string line)
    {
        if (line.Contains("done in", StringComparison.OrdinalIgnoreCase) && stage is not ("tts_model_load" or "tts_infer"))
            return;
        switch (stage)
        {
            case "config_load": SetAiProgress(stage, "Loading config...", 10, false, "Running", Brushes.SteelBlue); break;
            case "path_resolve": SetAiProgress(stage, "Resolving paths...", 12, false, "Running", Brushes.SteelBlue); break;
            case "model_check": SetAiProgress(stage, "Checking model files...", 15, false, "Running", Brushes.SteelBlue); break;
            case "tts_prepare": SetAiProgress(stage, "Preparing TTS backend...", 18, false, "Running", Brushes.SteelBlue); break;
            case "tts_model_load": SetAiProgress(stage, "Loading IndexTTS2 model...", null, true, "Waiting / loading model", Brushes.DarkOrange); break;
            case "tts_infer": SetAiProgress(stage, "Running IndexTTS2 inference...", null, true, "Waiting / inference", Brushes.DarkOrange); break;
            case "wav_load": SetAiProgress(stage, "Loading generated wav...", 55, false, "Running", Brushes.SteelBlue); break;
            case "audio_features": SetAiProgress(stage, "Extracting audio features...", 65, false, "Running", Brushes.SteelBlue); break;
            case "alignment": SetAiProgress(stage, "Aligning text and audio...", 72, false, "Running", Brushes.SteelBlue); break;
            case "morph_timeline": SetAiProgress(stage, "Generating morph timeline...", 82, false, "Running", Brushes.SteelBlue); break;
            case "validation": SetAiProgress(stage, "Validating frames...", 90, false, "Running", Brushes.SteelBlue); break;
            case "write_outputs": SetAiProgress(stage, "Writing outputs...", 96, false, "Running", Brushes.SteelBlue); break;
        }
    }

    private static string? ExtractRuntimeStage(string line)
    {
        var marker = "[AI_RUNTIME]";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var levelStart = line.IndexOf('[', start + marker.Length);
        if (levelStart < 0) return null;
        var levelEnd = line.IndexOf(']', levelStart + 1);
        if (levelEnd < 0) return null;
        var stageOpen = line.IndexOf('[', levelEnd + 1);
        if (stageOpen < 0) return null;
        var stageStart = stageOpen + 1;
        var stageEnd = line.IndexOf(']', stageStart);
        return stageEnd > stageStart ? line[stageStart..stageEnd] : null;
    }

    private void EnsureAiFlushTimer()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(EnsureAiFlushTimer));
            return;
        }
        if (!_aiFlushTimer.IsEnabled)
            _aiFlushTimer.Start();
    }

    private void AiFlushTimer_OnTick(object? sender, EventArgs e)
    {
        DrainPendingParsedLines();
        FlushAiText(AiLogBox, _pendingAiLog);
        FlushAiText(AiProcessOutputBox, _pendingAiProcess);
        if (_pendingAiLog.IsEmpty && _pendingAiProcess.IsEmpty && _pendingAiParsed.IsEmpty)
            _aiFlushTimer.Stop();
    }

    private void DrainPendingParsedLines()
    {
        var count = 0;
        while (count < MaxFlushLinesPerTick && _pendingAiParsed.TryDequeue(out var item))
        {
            ParseSetupLine(item.Line);
            ParseAiRuntimeLine(item.Stream, item.Line);
            count++;
        }
    }

    private void FlushAiText(TextBox box, ConcurrentQueue<string> pending)
    {
        var sb = new StringBuilder();
        var count = 0;
        while (count < MaxFlushLinesPerTick && pending.TryDequeue(out var line))
        {
            sb.AppendLine(line);
            count++;
        }
        if (sb.Length == 0)
            return;
        box.AppendText(sb.ToString());
        TrimTextBox(box, MaxAiTextChars, MaxAiLines);
        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();
    }

    private static void TrimTextBox(TextBox box, int maxChars, int maxLines)
    {
        var text = box.Text;
        if (text.Length <= maxChars && text.Split('\n').Length <= maxLines)
            return;
        var cutByChars = text.Length > maxChars ? text[^maxChars..] : text;
        var lines = cutByChars.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > maxLines)
            cutByChars = string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        box.Text = cutByChars;
    }

    private string BuildAiRuntimeCommandPreview(AiRuntimeOptions o)
    {
        var resolvedConfig = Path.Combine(o.OutDir, "ai_runtime.resolved.json");
        var offlineArg = o.OfflineMode ? " --offline" : " --allow-online";
        return $"\"{o.PythonExe}\" \"{Path.Combine(RepoPaths.FindRepoRoot(), "ai_runtime", "app.py")}\" --config \"{resolvedConfig}\" --verbose --print-progress{offlineArg}";
    }

    private static string QuoteForCommand(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void BrowseFileInto(TextBox target, string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog() == true) target.Text = dlg.FileName;
    }

    private static void BrowseFolderInto(TextBox target)
    {
        var dlg = new OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "Select Folder",
            Filter = "Folders|*.folder"
        };
        if (dlg.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(dir)) target.Text = dir;
        }
    }

    private void AppendLog(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        if (level != "JSON") AppendToTextBox(AppLogBox, line);
        if (level == "JSON")
        {
            AppendToTextBox(JsonLogBox, line);
            _rawJsonLines.Add(message);
        }
        if (level is "WARN" or "ERROR")
            AppendToTextBox(ErrorLogBox, line);
    }

    private static void AppendToTextBox(TextBox box, string line)
    {
        box.AppendText(line + Environment.NewLine);
        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();
    }
}
