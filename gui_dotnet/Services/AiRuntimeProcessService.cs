using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

public sealed class AiRuntimeProcessService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string LastCommandLine { get; private set; } = "";
    private Process? _runningProcess;

    private sealed class ManifestLoadOutcome
    {
        public AiRuntimeManifest? Manifest { get; set; }
        public string? RawText { get; set; }
        public string? ParseError { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public async Task<AiRuntimeResult> GenerateAsync(
        AiRuntimeOptions options,
        Action<string, string> onOutput,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Directory.CreateDirectory(options.OutDir);
        var resolvedConfig = Path.Combine(options.OutDir, "ai_runtime.resolved.json");
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(resolvedConfig, BuildResolvedConfigJson(options), utf8NoBom, ct);

        var python = string.IsNullOrWhiteSpace(options.PythonExe) ? "python" : options.PythonExe!;
        var args = new List<string>
        {
            Quote(Path.Combine(RepoPaths.FindRepoRoot(), "ai_runtime", "app.py")),
            "--config", Quote(resolvedConfig),
            "--text", Quote(options.Text),
            "--emotion", options.Emotion,
            "--out-dir", Quote(options.OutDir),
            "--fps", options.Fps.ToString(),
            "--character-id", options.CharacterId,
            "--morph-map", Quote(options.MorphMap),
            "--tts-backend", options.TtsBackend,
            "--emotion-strength", options.EmotionStrength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--blink-enabled", options.BlinkEnabled ? "true" : "false",
            "--verbose",
            "--print-progress",
        };
        if (!string.IsNullOrWhiteSpace(options.SkeletonTree)) args.AddRange(["--skeleton-tree", Quote(options.SkeletonTree!)]);
        if (!string.IsNullOrWhiteSpace(options.SpeakerWav)) args.AddRange(["--speaker-wav", Quote(options.SpeakerWav!)]);
        if (!string.IsNullOrWhiteSpace(options.EmotionPrompt)) args.AddRange(["--emotion-prompt", Quote(options.EmotionPrompt!)]);
        if (options.TargetDurationSec.HasValue) args.AddRange(["--target-duration-sec", options.TargetDurationSec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.DryRun || options.TtsBackend == "dryrun") args.Add("--dry-run");
        if (options.SkipTts) args.Add("--skip-tts");
        if (!string.IsNullOrWhiteSpace(options.ExistingWav)) args.AddRange(["--wav", Quote(options.ExistingWav!)]);
        if (options.OfflineMode) args.Add("--offline");
        if (options.AllowOnline) args.Add("--allow-online");
        if (options.UseGeneratedLocalConfig) args.Add("--use-generated-local-config");

        var pythonFile = python;
        var pythonPrefixArgs = "";
        if (string.Equals(python.Trim(), "py -3", StringComparison.OrdinalIgnoreCase))
        {
            pythonFile = "py";
            pythonPrefixArgs = "-3 ";
        }
        LastCommandLine = Quote(pythonFile) + " " + pythonPrefixArgs + string.Join(" ", args);
        var psi = new ProcessStartInfo
        {
            FileName = pythonFile,
            Arguments = pythonPrefixArgs + "-u " + string.Join(" ", args),
            WorkingDirectory = RepoPaths.FindRepoRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        var repoRoot = RepoPaths.FindRepoRoot();
        var hfHome = Path.Combine(repoRoot, "models", "hf_cache");
        var hfTransformersCache = Path.Combine(hfHome, "transformers");
        var hfHubCache = Path.Combine(hfHome, "hub");
        Directory.CreateDirectory(hfHome);
        Directory.CreateDirectory(hfTransformersCache);
        Directory.CreateDirectory(hfHubCache);
        psi.Environment["HF_HOME"] = hfHome;
        psi.Environment["TRANSFORMERS_CACHE"] = hfTransformersCache;
        psi.Environment["HF_HUB_CACHE"] = hfHubCache;
        if (!string.IsNullOrWhiteSpace(options.IndexTtsModelDir))
        {
            psi.Environment["INDEXTTS_MODEL_DIR"] = options.IndexTtsModelDir!;
            psi.Environment["W2V_BERT_LOCAL_DIR"] = Path.Combine(options.IndexTtsModelDir!, "w2v-bert-2.0");
            psi.Environment["MASKGCT_LOCAL_DIR"] = Path.Combine(options.IndexTtsModelDir!, "MaskGCT");
            psi.Environment["BIGVGAN_LOCAL_DIR"] = Path.Combine(options.IndexTtsModelDir!, "BigVGAN");
        }
        if (options.OfflineMode)
        {
            psi.Environment["HF_HUB_OFFLINE"] = "1";
            psi.Environment["TRANSFORMERS_OFFLINE"] = "1";
            psi.Environment["HF_HUB_DISABLE_XET"] = "1";
        }
        else
        {
            psi.Environment["HF_HUB_OFFLINE"] = "0";
            psi.Environment["TRANSFORMERS_OFFLINE"] = "0";
            psi.Environment["HF_HUB_DISABLE_XET"] = "0";
        }
        if (!string.IsNullOrWhiteSpace(options.IndexTtsRepo))
        {
            psi.Environment["PYTHONPATH"] = options.IndexTtsRepo + Path.PathSeparator +
                                        (psi.Environment.TryGetValue("PYTHONPATH", out var pyPath) ? pyPath : "");
        }
        LastCommandLine = Quote(pythonFile) + " " + pythonPrefixArgs + "-u " + string.Join(" ", args);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _runningProcess = process;
        process.Start();
        await using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    onOutput("STDERR", "[AI_RUNTIME][CANCELLED][process] Python process tree killed.");
                }
            }
            catch { /* ignored */ }
        });

        var outTask = ReadStreamAsync(process.StandardOutput, "STDOUT", stdout, onOutput, ct);
        var errTask = ReadStreamAsync(process.StandardError, "STDERR", stderr, onOutput, ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        _runningProcess = null;
        sw.Stop();

        var manifestPath = Path.Combine(options.OutDir, "manifest.json");
        var failedManifestPath = Path.Combine(options.OutDir, "manifest_failed.json");
        AiRuntimeManifest? manifest = null;
        string? rawManifestText = null;
        string? manifestParseError = null;
        string? manifestErrorMessage = null;
        var manifestToRead = File.Exists(manifestPath) ? manifestPath : (File.Exists(failedManifestPath) ? failedManifestPath : "");
        if (!string.IsNullOrEmpty(manifestToRead))
        {
            var loaded = await SafeLoadManifestAsync(manifestToRead, ct);
            manifest = loaded.Manifest;
            rawManifestText = loaded.RawText;
            manifestParseError = loaded.ParseError;
            manifestErrorMessage = loaded.ErrorMessage;
            if (loaded.ParseError != null && Path.GetFileName(manifestToRead).Equals("manifest_failed.json", StringComparison.OrdinalIgnoreCase))
                onOutput("STDERR", "Failed to parse manifest_failed.json, showing raw file content instead.");
        }

        var stdErrText = stderr.ToString();
        var stdOutText = stdout.ToString();
        var errorMessage = process.ExitCode == 0
            ? null
            : ChooseErrorMessage(stdErrText, stdOutText, manifestErrorMessage, manifestParseError, process.ExitCode);

        return new AiRuntimeResult
        {
            Success = process.ExitCode == 0 && File.Exists(manifestPath),
            ExitCode = process.ExitCode,
            OutDir = options.OutDir,
            WavPath = Path.Combine(options.OutDir, "generated.wav"),
            FramesPath = Path.Combine(options.OutDir, "frames.jsonl"),
            ManifestPath = manifestToRead,
            FailedManifestPath = File.Exists(failedManifestPath) ? failedManifestPath : null,
            Manifest = manifest,
            RawManifestText = rawManifestText,
            ManifestParseError = manifestParseError,
            Stdout = stdOutText,
            Stderr = stdErrText,
            Elapsed = sw.Elapsed,
            ErrorMessage = errorMessage,
            CommandLine = LastCommandLine,
        };
    }

    private static async Task<ManifestLoadOutcome> SafeLoadManifestAsync(string path, CancellationToken ct)
    {
        var outcome = new ManifestLoadOutcome();
        if (!File.Exists(path))
            return outcome;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            text = text.TrimStart('\uFEFF');
        }
        catch (Exception ex)
        {
            outcome.ParseError = "Could not read manifest: " + ex.Message;
            return outcome;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            outcome.ParseError = "Manifest file is empty: " + path;
            return outcome;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_message", out var err))
                outcome.ErrorMessage = err.GetString();
            if (root.TryGetProperty("exit_status", out var exit) &&
                string.Equals(exit.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
            {
                outcome.RawText = text.Length > 2000 ? text[..2000] : text;
                return outcome;
            }
            outcome.Manifest = JsonSerializer.Deserialize<AiRuntimeManifest>(text);
        }
        catch (Exception ex)
        {
            outcome.ParseError = ex.Message;
            outcome.RawText = text.Length > 2000 ? text[..2000] : text;
        }
        return outcome;
    }

    private static string ChooseErrorMessage(string stderr, string stdout, string? manifestError, string? manifestParseError, int exitCode)
    {
        var stderrTail = LastNonEmptyBlock(stderr);
        if (!string.IsNullOrWhiteSpace(stderrTail))
            return stderrTail;
        var aiError = stdout.Split(Environment.NewLine)
            .Where(line => line.Contains("[AI_RUNTIME][ERROR]", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(aiError))
            return aiError.Trim();
        if (!string.IsNullOrWhiteSpace(manifestError))
            return manifestError!;
        if (!string.IsNullOrWhiteSpace(manifestParseError))
            return manifestParseError!;
        return $"ai_runtime exited with {exitCode}";
    }

    private static string LastNonEmptyBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (lines.Count == 0)
            return "";
        return string.Join(Environment.NewLine, lines.TakeLast(Math.Min(12, lines.Count)));
    }

    private static async Task ReadStreamAsync(StreamReader reader, string stream, StringBuilder sink, Action<string, string> onOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            sink.AppendLine(line);
            onOutput(stream, line);
        }
    }

    private static string BuildResolvedConfigJson(AiRuntimeOptions o)
    {
        var data = new
        {
            tts = new
            {
                backend = o.TtsBackend,
                indextts_repo = o.IndexTtsRepo ?? "",
                indextts_model_dir = o.IndexTtsModelDir ?? "",
                indextts_config = o.IndexTtsConfig ?? "",
                indextts_infer_script = o.IndexTtsInferScript ?? "",
                python_executable = o.PythonExe ?? "",
                default_speaker_wav = o.SpeakerWav ?? "",
                emo_alpha = 0.6,
                use_fp16 = o.UseFp16,
                use_cuda_kernel = o.UseCudaKernel,
                use_deepspeed = o.UseDeepSpeed,
                use_random = o.UseRandom,
                fallback_to_dryrun = o.FallbackToDryrun,
            },
            runtime = new
            {
                fps = o.Fps,
                character_id = o.CharacterId,
                morph_map = o.MorphMap,
                skeleton_tree = o.SkeletonTree ?? "",
                output_root = Path.Combine(RepoPaths.FindRepoRoot(), "output", "ai_sessions"),
                gui_mode = o.GuiMode,
                mode = o.GuiMode,
            },
            face = new
            {
                emotion_strength = o.EmotionStrength,
                blink_enabled = o.BlinkEnabled,
            },
            alignment = new { backend = "heuristic" },
            skeleton = new { drive_skeleton_by_default = false },
        };
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public bool TryStopRunningProcessTree(Action<string, string>? onOutput = null)
    {
        try
        {
            var process = _runningProcess;
            if (process == null || process.HasExited)
                return false;
            process.Kill(entireProcessTree: true);
            onOutput?.Invoke("STDERR", "[AI_RUNTIME][CANCELLED][process] Stop requested; Python process tree killed.");
            return true;
        }
        catch (Exception ex)
        {
            onOutput?.Invoke("STDERR", "[AI_RUNTIME][WARN][process] Failed to kill process tree: " + ex.Message);
            return false;
        }
    }
}
