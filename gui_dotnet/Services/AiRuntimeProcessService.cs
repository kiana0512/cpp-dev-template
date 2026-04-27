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
        public string? RealExceptionMessage { get; set; }
        public string? NativeExitHex { get; set; }
        public string? LastSuccessMarker { get; set; }
        public string? ConstructDebugLog { get; set; }
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
            "--text-normalizer", options.TextNormalizer,
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
        psi.Environment["INDEXTTS_TEXT_NORMALIZER"] = string.IsNullOrWhiteSpace(options.TextNormalizer) ? "fallback" : options.TextNormalizer;
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        psi.Environment.Remove("PYTHONHOME");
        onOutput("STDOUT", "[AI][INFO] Text normalizer: " + psi.Environment["INDEXTTS_TEXT_NORMALIZER"]);
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
        var pythonPathParts = new[]
        {
            options.IndexTtsRepo ?? "",
            repoRoot,
            Path.Combine(repoRoot, "ai_runtime"),
        }.Where(x => !string.IsNullOrWhiteSpace(x));
        psi.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPathParts);
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
        string? manifestRealExceptionMessage = null;
        string? nativeExitHex = null;
        string? lastSuccessMarker = null;
        string? constructDebugLog = null;
        var manifestToRead = File.Exists(manifestPath) ? manifestPath : (File.Exists(failedManifestPath) ? failedManifestPath : "");
        if (!string.IsNullOrEmpty(manifestToRead))
        {
            var loaded = await SafeLoadManifestAsync(manifestToRead, ct);
            manifest = loaded.Manifest;
            rawManifestText = loaded.RawText;
            manifestParseError = loaded.ParseError;
            manifestErrorMessage = loaded.ErrorMessage;
            manifestRealExceptionMessage = loaded.RealExceptionMessage;
            nativeExitHex = loaded.NativeExitHex;
            lastSuccessMarker = loaded.LastSuccessMarker;
            constructDebugLog = loaded.ConstructDebugLog;
            if (loaded.ParseError != null && Path.GetFileName(manifestToRead).Equals("manifest_failed.json", StringComparison.OrdinalIgnoreCase))
                onOutput("STDERR", "Failed to parse manifest_failed.json, showing raw file content instead.");
        }

        var stdErrText = stderr.ToString();
        var stdOutText = stdout.ToString();
        var errorMessage = process.ExitCode == 0
            ? null
            : ChooseErrorMessage(stdErrText, stdOutText, manifestRealExceptionMessage, manifestErrorMessage, manifestParseError, process.ExitCode, options.TextNormalizer, nativeExitHex, lastSuccessMarker, constructDebugLog);

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
            if (root.TryGetProperty("real_exception_message", out var realErr))
                outcome.RealExceptionMessage = realErr.GetString();
            if (root.TryGetProperty("native_exit_hex", out var nativeHex))
                outcome.NativeExitHex = nativeHex.GetString();
            if (root.TryGetProperty("last_success_marker", out var marker))
                outcome.LastSuccessMarker = marker.GetString();
            if (root.TryGetProperty("construct_debug_log", out var debugLog))
                outcome.ConstructDebugLog = debugLog.GetString();
            if (root.TryGetProperty("diagnostics", out var diagnostics))
            {
                if (string.IsNullOrWhiteSpace(outcome.NativeExitHex) && diagnostics.TryGetProperty("native_exit_hex", out var diagNativeHex))
                    outcome.NativeExitHex = diagNativeHex.GetString();
                if (string.IsNullOrWhiteSpace(outcome.LastSuccessMarker) && diagnostics.TryGetProperty("last_success_marker", out var diagMarker))
                    outcome.LastSuccessMarker = diagMarker.GetString();
                if (string.IsNullOrWhiteSpace(outcome.ConstructDebugLog) && diagnostics.TryGetProperty("construct_debug_log", out var diagDebugLog))
                    outcome.ConstructDebugLog = diagDebugLog.GetString();
            }
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

    private static string ChooseErrorMessage(string stderr, string stdout, string? manifestRealException, string? manifestError, string? manifestParseError, int exitCode, string textNormalizer, string? nativeExitHex, string? lastSuccessMarker, string? constructDebugLog)
    {
        var combined = stderr + "\n" + stdout + "\n" + (manifestError ?? "");
        var normalizerMode = string.IsNullOrWhiteSpace(textNormalizer) ? "auto" : textNormalizer.Trim().ToLowerInvariant();
        if (IsNativeAccessViolation(exitCode, combined, manifestRealException, manifestError, nativeExitHex))
        {
            var marker = string.IsNullOrWhiteSpace(lastSuccessMarker) ? "" : " Last marker: " + lastSuccessMarker + ".";
            var log = string.IsNullOrWhiteSpace(constructDebugLog) ? " See construct_debug.jsonl / wrapper stderr." : " See " + constructDebugLog + " / wrapper stderr.";
            return "Native crash 0xC0000005 during IndexTTS2 construction." + marker + log;
        }
        if (!string.IsNullOrWhiteSpace(manifestRealException))
            return manifestRealException!;
        if (!string.IsNullOrWhiteSpace(manifestError))
            return manifestError!;
        if (normalizerMode == "wetext" && ContainsTextNormalizerFailure(combined))
            return "Text normalizer failed. Try INDEXTTS_TEXT_NORMALIZER=fallback on Windows.";
        if (combined.Contains("Text normalizer fallback did not take effect", StringComparison.OrdinalIgnoreCase))
            return "Text normalizer fallback did not take effect. Check that INDEXTTS_TEXT_NORMALIZER is set before importing IndexTTS2 and front.py has no top-level wetext import.";
        var aiError = stdout.Split(Environment.NewLine)
            .Where(line => line.Contains("[AI_RUNTIME][ERROR]", StringComparison.OrdinalIgnoreCase))
            .Where(line => !IsFallbackNormalizerWarning(line))
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(aiError))
            return aiError.Trim();
        var stderrTail = LastNonEmptyBlock(stderr);
        if (!string.IsNullOrWhiteSpace(stderrTail))
            return stderrTail;
        if (!string.IsNullOrWhiteSpace(manifestParseError))
            return manifestParseError!;
        return $"ai_runtime exited with {exitCode}";
    }

    private static bool IsNativeAccessViolation(int exitCode, string combined, string? manifestRealException, string? manifestError, string? nativeExitHex)
    {
        if (exitCode == unchecked((int)0xC0000005))
            return true;
        var text = combined + "\n" + (manifestRealException ?? "") + "\n" + (manifestError ?? "") + "\n" + (nativeExitHex ?? "");
        return text.Contains("0xC0000005", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("3221225477", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("NativeAccessViolation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("native process crash/access violation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFallbackNormalizerWarning(string text)
    {
        return text.Contains("using fallback normalizer", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("fallback mode enabled", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TextNormalizer loaded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTextNormalizerFailure(string text)
    {
        return text.Contains("_kaldifst", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("kaldifst", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("wetext", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DLL load failed", StringComparison.OrdinalIgnoreCase);
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
                text_normalizer = string.IsNullOrWhiteSpace(o.TextNormalizer) ? "fallback" : o.TextNormalizer,
                auto_try_local_api = false,
                auto_try_legacy = false,
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
