using System.IO;

namespace VhSenderGui.Services;

/// <summary>Locate repository root (directory containing configs/presets.json) for reliable paths when running from bin/.</summary>
public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var preset = Path.Combine(dir.FullName, "configs", "presets.json");
            if (File.Exists(preset))
                return dir.FullName;
            dir = dir.Parent;
        }

        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        dir = cwd;
        while (dir != null)
        {
            var preset = Path.Combine(dir.FullName, "configs", "presets.json");
            if (File.Exists(preset))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static string PresetsJson => Path.Combine(FindRepoRoot(), "configs", "presets.json");
    public static string SampleFrameJson => Path.Combine(FindRepoRoot(), "configs", "sample_frame.json");
    public static string BlendshapeMapReference => Path.Combine(FindRepoRoot(), "configs", "blendshape_map_yyb_miku.json");
    public static string DefaultOutputJsonl => Path.Combine(FindRepoRoot(), "outputs", "frames.jsonl");
    public static string LogsDir => Path.Combine(FindRepoRoot(), "outputs", "logs");
}
