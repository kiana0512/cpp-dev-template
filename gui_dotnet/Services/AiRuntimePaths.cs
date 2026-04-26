using System.IO;

namespace VhSenderGui.Services;

public sealed class AiRuntimePaths
{
    public string RepoRoot { get; init; } = "";
    public string AiRuntimeAppPy { get; init; } = "";
    public string DefaultPythonExe { get; init; } = "python";
    public string DefaultModelDir { get; init; } = "";
    public string DefaultIndexTtsConfig { get; init; } = "";
    public string DefaultIndexTtsRepo { get; init; } = "";
    public string DefaultMorphMap { get; init; } = "";
    public string DefaultSkeletonTree { get; init; } = "";
    public string DefaultOutDir { get; init; } = "";

    public static AiRuntimePaths Create()
    {
        var root = RepoPaths.FindRepoRoot();
        var thirdParty = Path.Combine(root, "third_party", "index-tts");
        var envRepo = Environment.GetEnvironmentVariable("INDEXTTS_REPO") ?? "";
        var indexTtsUvPy = Path.Combine(thirdParty, ".venv", "Scripts", "python.exe");
        var envPython = Environment.GetEnvironmentVariable("INDEXTTS_PYTHON") ?? "";
        var venvPy = Path.Combine(root, ".venv", "Scripts", "python.exe");
        var virtualEnv = Environment.GetEnvironmentVariable("VIRTUAL_ENV") ?? "";
        var virtualEnvPy = string.IsNullOrWhiteSpace(virtualEnv) ? "" : Path.Combine(virtualEnv, "Scripts", "python.exe");
        var python = File.Exists(indexTtsUvPy)
            ? indexTtsUvPy
            : (File.Exists(venvPy)
            ? venvPy
            : (!string.IsNullOrWhiteSpace(envPython) && File.Exists(envPython)
                ? envPython
                : (File.Exists(virtualEnvPy)
                    ? virtualEnvPy
                    : (CommandExists("python.exe") ? "python" : (CommandExists("py.exe") ? "py -3" : "python")))));
        var skeleton = Directory.GetFiles(Path.Combine(root, "configs"), "skeleton_tree*.json").OrderBy(x => x).FirstOrDefault() ?? "";
        return new AiRuntimePaths
        {
            RepoRoot = root,
            AiRuntimeAppPy = Path.Combine(root, "ai_runtime", "app.py"),
            DefaultPythonExe = python,
            DefaultModelDir = Path.Combine(root, "models", "IndexTTS-2"),
            DefaultIndexTtsConfig = Path.Combine(root, "models", "IndexTTS-2", "config.yaml"),
            DefaultIndexTtsRepo = Directory.Exists(thirdParty) ? thirdParty : envRepo,
            DefaultMorphMap = Path.Combine(root, "configs", "blendshape_map_yyb_miku.json"),
            DefaultSkeletonTree = skeleton,
            DefaultOutDir = Path.Combine(root, "output", "ai_sessions", "gui_latest"),
        };
    }

    private static bool CommandExists(string exeName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Any(p =>
        {
            try { return File.Exists(Path.Combine(p, exeName)); }
            catch { return false; }
        });
    }
}
