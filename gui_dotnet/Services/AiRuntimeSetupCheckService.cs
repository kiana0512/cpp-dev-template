using System.Diagnostics;
using System.IO;
using System.Text;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

public static class AiRuntimeSetupCheckService
{
    public static async Task<AiSetupCheckResult> CheckAsync(
        AiRuntimeOptions options,
        string basicBackend,
        Action<string, string> onProcessOutput,
        CancellationToken ct)
    {
        var result = new AiSetupCheckResult();

        var realTts = string.Equals(basicBackend, "real IndexTTS2", StringComparison.OrdinalIgnoreCase);
        var existingWavMode = string.Equals(basicBackend, "use existing WAV", StringComparison.OrdinalIgnoreCase);

        onProcessOutput("STDOUT", "Step 1: Path check");
        onProcessOutput("STDOUT", "Project root: " + RepoPaths.FindRepoRoot());
        onProcessOutput("STDOUT", "Python path: " + (options.PythonExe ?? ""));
        onProcessOutput("STDOUT", "Python exists: " + CommandOrFileExists(options.PythonExe ?? ""));
        onProcessOutput("STDOUT", "IndexTTS repo: " + (options.IndexTtsRepo ?? ""));
        onProcessOutput("STDOUT", "Repo exists: " + Directory.Exists(options.IndexTtsRepo ?? ""));
        onProcessOutput("STDOUT", "Model dir: " + (options.IndexTtsModelDir ?? ""));
        onProcessOutput("STDOUT", "Model dir exists: " + Directory.Exists(options.IndexTtsModelDir ?? ""));
        onProcessOutput("STDOUT", "Config: " + (options.IndexTtsConfig ?? ""));
        onProcessOutput("STDOUT", "Config exists: " + File.Exists(options.IndexTtsConfig ?? ""));
        onProcessOutput("STDOUT", "Speaker WAV: " + (options.SpeakerWav ?? ""));
        onProcessOutput("STDOUT", "Speaker WAV exists: " + File.Exists(options.SpeakerWav ?? ""));
        onProcessOutput("STDOUT", "Offline mode: " + options.OfflineMode);

        var model = AiRuntimeModelCheckService.Check(options.IndexTtsModelDir ?? "", options.IndexTtsConfig ?? "", options.IndexTtsRepo);
        result.ModelOk = options.OfflineMode ? model.CanRunOffline : (model.ModelDirExists && model.ConfigExists);
        onProcessOutput("STDOUT", "Main model: " + (model.IndexTts2Ok ? "OK" : "Missing"));
        onProcessOutput("STDOUT", "w2v-bert-2.0: " + (model.W2vBertOk ? "OK" : "Missing"));
        onProcessOutput("STDOUT", "MaskGCT: " + (model.MaskGctOk ? "OK" : "Missing"));
        onProcessOutput("STDOUT", "Remote refs: " + (model.RemoteRefsOk ? "OK" : "Found"));
        onProcessOutput("STDOUT", "Offline runnable: " + (model.CanRunOffline ? "yes" : "no"));
        onProcessOutput("STDOUT", "Local model completeness: " + (model.LooksLocalComplete ? "OK" : "WARN"));
        onProcessOutput("STDOUT", "Remote references detected: " + (model.RemoteReferences.Count == 0 ? "none" : string.Join(" | ", model.RemoteReferences.Take(20))));
        onProcessOutput("STDOUT", "Missing local files: " + (model.MissingOrSuspiciousFiles.Count == 0 ? "none" : string.Join(", ", model.MissingOrSuspiciousFiles)));
        onProcessOutput("STDOUT", "Recommendations: " + (model.Recommendations.Count == 0 ? model.Recommendation : string.Join(" | ", model.Recommendations)));
        foreach (var warning in model.Warnings)
            if (realTts) result.Warnings.Add(warning);
        if (options.OfflineMode && model.RemoteReferences.Count > 0)
            result.Warnings.Add("Config may still point to remote HF model names. Please ensure paths are local.");
        if (realTts && !model.ModelDirExists)
            result.Errors.Add("Model dir does not exist: " + options.IndexTtsModelDir);
        if (realTts && !model.ConfigExists)
            result.Errors.Add("config.yaml not found. Model download may be incomplete: " + options.IndexTtsConfig);
        if (realTts && options.OfflineMode && !model.W2vBertOk)
            result.Errors.Add("Local dependency missing: w2v-bert-2.0. Run: modelscope download --model AI-ModelScope/w2v-bert-2.0 --local_dir .\\models\\IndexTTS-2\\w2v-bert-2.0");
        if (realTts && options.OfflineMode && !model.MaskGctOk)
            result.Errors.Add("Local dependency missing: MaskGCT. Run: modelscope download --model amphion/MaskGCT --local_dir .\\models\\IndexTTS-2\\MaskGCT");
        if (realTts && options.OfflineMode && !model.RemoteRefsOk)
            result.Errors.Add("IndexTTS2 source still contains remote HF references. Run local patch first: .\\scripts\\patch_indextts2_local_paths.ps1");

        result.SpeakerOk = !realTts || (!string.IsNullOrWhiteSpace(options.SpeakerWav) && File.Exists(options.SpeakerWav));
        result.ExistingWavOk = !existingWavMode || (!string.IsNullOrWhiteSpace(options.ExistingWav) && File.Exists(options.ExistingWav));
        if (realTts && !result.SpeakerOk)
            result.Errors.Add("Real IndexTTS2 requires Speaker WAV / spk_audio_prompt. Please choose a reference voice wav first.");
        if (existingWavMode && !result.ExistingWavOk)
            result.Errors.Add("Use existing WAV mode requires an existing WAV file.");

        var python = string.IsNullOrWhiteSpace(options.PythonExe) ? "python" : options.PythonExe!;
        var pythonCanRun = CommandOrFileExists(python);
        if (!pythonCanRun)
            result.Errors.Add("Selected Python does not exist or is not a supported command: " + python);
        var repo = options.IndexTtsRepo ?? "";
        if (!string.IsNullOrWhiteSpace(repo))
        {
            if (!Directory.Exists(repo))
                result.Errors.Add("IndexTTS repo directory does not exist: " + repo);
            else if (!File.Exists(Path.Combine(repo, "indextts", "infer_v2.py")))
                result.Warnings.Add("IndexTTS repo exists but indextts/infer_v2.py was not found: " + repo);
        }
        else if (realTts)
            result.Warnings.Add("Repo is empty. The selected Python must already have indextts installed.");

        onProcessOutput("STDOUT", "Step 2: Python executable check");
        if (pythonCanRun)
        {
            var pythonCheck = await RunPythonCheckAsync(python, "import sys; print(sys.executable); print(sys.version)", repo, "", options.OfflineMode, onProcessOutput, ct);
            if (!pythonCheck.ok)
                result.Errors.Add("Selected Python could not run. stderr: " + pythonCheck.stderr.Trim());
            if ((pythonCheck.stdout + pythonCheck.stderr).Contains(@"D:\anaconda3\envs\llm", StringComparison.OrdinalIgnoreCase) &&
                !(python.Contains(@"D:\anaconda3\envs\llm", StringComparison.OrdinalIgnoreCase)))
                result.Warnings.Add("Check Setup is using conda llm unexpectedly; please verify Python path binding.");
        }

        result.PythonImportOk = !realTts || (pythonCanRun && await CheckPythonImportAsync(python, repo, options.OfflineMode, onProcessOutput, result, ct));

        if (!realTts)
            result.PythonImportOk = true;
        if (string.Equals(basicBackend, "dryrun", StringComparison.OrdinalIgnoreCase))
        {
            result.ModelOk = true;
            result.SpeakerOk = true;
            result.ExistingWavOk = true;
        }
        if (existingWavMode)
        {
            result.ModelOk = true;
            result.PythonImportOk = true;
            result.SpeakerOk = true;
        }

        result.OverallOk = result.Errors.Count == 0 &&
                           (basicBackend == "dryrun" || basicBackend == "use existing WAV" || (result.ModelOk && result.PythonImportOk && result.SpeakerOk));
        result.Summary = result.OverallOk
            ? (result.Warnings.Count > 0 ? "Setup Warning" : "Setup OK")
            : "Setup Error";
        return result;
    }

    private static bool CommandOrFileExists(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (File.Exists(value)) return true;
        return value.Equals("python", StringComparison.OrdinalIgnoreCase) || value.Equals("py -3", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> CheckPythonImportAsync(
        string python,
        string repo,
        bool offlineMode,
        Action<string, string> onProcessOutput,
        AiSetupCheckResult result,
        CancellationToken ct)
    {
        onProcessOutput("STDOUT", "Step 3: IndexTTS import check");
        var script = "import sys; print('PYTHON=', sys.executable); from indextts.infer_v2 import IndexTTS2; print('IMPORT_OK')";
        var first = await RunPythonCheckAsync(python, script, repo, "", offlineMode, onProcessOutput, ct);
        if (first.ok)
        {
            result.Warnings.Add("IMPORT_OK using selected Python.");
            await CheckDependencyQuickAsync(python, repo, offlineMode, onProcessOutput, result, ct);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo))
        {
            var second = await RunPythonCheckAsync(python, script, repo, repo, offlineMode, onProcessOutput, ct);
            if (second.ok)
            {
                result.Warnings.Add("IMPORT_OK after adding IndexTTS repo to PYTHONPATH.");
                await CheckDependencyQuickAsync(python, repo, offlineMode, onProcessOutput, result, ct);
                return true;
            }
            result.Errors.Add("IMPORT_FAILED even with PYTHONPATH=repo. stderr: " + second.stderr.Trim());
            result.Errors.Add("Fix: cd D:\\RT\\ue5-virtual-human-bridge\\third_party\\index-tts; uv sync --default-index \"https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple\"");
            result.Errors.Add("Or: uv sync --all-extras --default-index \"https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple\"");
            return false;
        }
        result.Errors.Add("IMPORT_FAILED. stderr: " + first.stderr.Trim());
        result.Errors.Add("Fix: select the IndexTTS2 uv Python or run uv sync in third_party/index-tts.");
        return false;
    }

    private static async Task CheckDependencyQuickAsync(string python, string repo, bool offlineMode, Action<string, string> onProcessOutput, AiSetupCheckResult result, CancellationToken ct)
    {
        onProcessOutput("STDOUT", "Step 4: Dependency quick check");
        var script = "import transformers; print('transformers', transformers.__version__); from transformers.cache_utils import OffloadedCache; print('OffloadedCache OK')";
        var check = await RunPythonCheckAsync(python, script, repo, repo, offlineMode, onProcessOutput, ct);
        if (!check.ok)
        {
            result.Errors.Add("Dependency quick check failed. Current Python is not the correct IndexTTS2 environment, or transformers version is incompatible.");
            if ((check.stdout + check.stderr).Contains("OffloadedCache", StringComparison.OrdinalIgnoreCase))
                result.Errors.Add("OffloadedCache import failure: use third_party/index-tts/.venv/Scripts/python.exe or run uv sync.");
        }
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunPythonCheckAsync(
        string python,
        string script,
        string cwdRepo,
        string pythonPath,
        bool offlineMode,
        Action<string, string> onProcessOutput,
        CancellationToken ct)
    {
        var file = python;
        var prefix = "";
        if (string.Equals(python.Trim(), "py -3", StringComparison.OrdinalIgnoreCase))
        {
            file = "py";
            prefix = "-3 ";
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = prefix + "-u -c \"" + script.Replace("\"", "\\\"") + "\"",
            WorkingDirectory = !string.IsNullOrWhiteSpace(cwdRepo) && Directory.Exists(cwdRepo) ? cwdRepo : RepoPaths.FindRepoRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        if (offlineMode)
        {
            psi.Environment["HF_HUB_OFFLINE"] = "1";
            psi.Environment["TRANSFORMERS_OFFLINE"] = "1";
            psi.Environment["HF_HUB_DISABLE_XET"] = "1";
        }
        if (!string.IsNullOrWhiteSpace(pythonPath))
            psi.Environment["PYTHONPATH"] = pythonPath + Path.PathSeparator + (psi.Environment.TryGetValue("PYTHONPATH", out var existing) ? existing : "");

        onProcessOutput("STDOUT", "Check Setup command: python=" + python);
        onProcessOutput("STDOUT", "cwd=" + psi.WorkingDirectory);
        onProcessOutput("STDOUT", "PYTHONPATH=" + (pythonPath == "" ? "(unchanged)" : pythonPath));
        onProcessOutput("STDOUT", "offline env=" + (offlineMode ? "HF_HUB_OFFLINE=1,TRANSFORMERS_OFFLINE=1,HF_HUB_DISABLE_XET=1" : "disabled"));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python check.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadLinesAsync(p.StandardOutput, "STDOUT", stdout, onProcessOutput, timeoutCts.Token);
        var stderrTask = ReadLinesAsync(p.StandardError, "STDERR", stderr, onProcessOutput, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* ignored */ }
            onProcessOutput("STDERR", "Check Setup timed out after 60 seconds. Python process tree killed.");
            return (false, "", "timeout after 60 seconds");
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();
        if (p.ExitCode == 0)
            onProcessOutput("STDOUT", stdoutText.Contains("IMPORT_OK") ? "IMPORT_OK" : "CHECK_OK");
        else
            onProcessOutput("STDERR", stdoutText.Contains("IMPORT_OK") ? "CHECK_FAILED_AFTER_IMPORT" : "CHECK_FAILED");
        return (p.ExitCode == 0, stdoutText, stderrText);
    }

    private static async Task ReadLinesAsync(StreamReader reader, string stream, StringBuilder sink, Action<string, string> onProcessOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            sink.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
                onProcessOutput(stream, line);
        }
    }
}
