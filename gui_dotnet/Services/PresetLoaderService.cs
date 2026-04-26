using System.IO;
using System.Text.Json;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

public static class PresetLoaderService
{
    /// <summary>Built-in presets when presets.json is missing or invalid (matches former GUI defaults).</summary>
    public static List<FramePreset> BuiltinPresets() =>
    [
        new() { Name = "Greeting", Text = "你好，我是Miku", Emotion = "happy", Phoneme = "a", Rms = 0.45, Confidence = 0.90 },
        new() { Name = "Happy", Text = "今天很开心", Emotion = "happy", Phoneme = "i", Rms = 0.50, Confidence = 0.92, HeadPitch = -2, HeadRoll = 2 },
        new() { Name = "Sad", Text = "我有一点难过", Emotion = "sad", Phoneme = "e", Rms = 0.25, Confidence = 0.88 },
        new() { Name = "Angry", Text = "我有点生气", Emotion = "angry", Phoneme = "a", Rms = 0.60, Confidence = 0.87, HeadPitch = -3 },
        new() { Name = "Surprise", Text = "哇，真的吗", Emotion = "surprised", Phoneme = "a", Rms = 0.55, Confidence = 0.91, HeadPitch = -5 },
        new() { Name = "Neutral", Text = "我在认真听", Emotion = "neutral", Phoneme = "rest", Rms = 0.10, Confidence = 1.00 },
        new() { Name = "AskAgain", Text = "请再说一遍", Emotion = "neutral", Phoneme = "e", Rms = 0.30, Confidence = 0.85, HeadPitch = 2, HeadYaw = 5 },
        new() { Name = "Laugh", Text = "哈哈，真有趣", Emotion = "happy", Phoneme = "a", Rms = 0.65, Confidence = 0.93, HeadYaw = -2, HeadRoll = 3 },
        new() { Name = "Think", Text = "让我想一想", Emotion = "neutral", Phoneme = "u", Rms = 0.20, Confidence = 0.80, HeadPitch = 3, HeadYaw = 8 },
        new() { Name = "Bye", Text = "再见，下次见", Emotion = "happy", Phoneme = "a", Rms = 0.40, Confidence = 0.90, HeadYaw = -3, HeadRoll = -1 },
    ];

    public static List<FramePreset> LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("presets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("missing 'presets' array");

        var list = new List<FramePreset>();
        foreach (var p in arr.EnumerateArray())
        {
            var fp = new FramePreset
            {
                Name = p.GetPropertyOrDefault("name", "Unnamed"),
                Text = p.GetPropertyOrDefault("text", ""),
                Emotion = p.GetPropertyOrDefault("emotion", "neutral"),
                Phoneme = p.GetPropertyOrDefault("phoneme", "rest"),
                Rms = p.GetDoubleOrDefault("rms", 0.3),
                Confidence = p.GetDoubleOrDefault("confidence", 0.9),
            };
            if (p.TryGetProperty("head_pose", out var hp))
            {
                fp.HeadPitch = hp.GetDoubleOrDefault("pitch", 0);
                fp.HeadYaw = hp.GetDoubleOrDefault("yaw", 0);
                fp.HeadRoll = hp.GetDoubleOrDefault("roll", 0);
            }
            list.Add(fp);
        }
        return list;
    }
}

file static class JsonElementExt
{
    public static string GetPropertyOrDefault(this JsonElement e, string name, string def)
    {
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? def : def;
    }

    public static double GetDoubleOrDefault(this JsonElement e, string name, double def)
    {
        if (!e.TryGetProperty(name, out var p)) return def;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            _ => def,
        };
    }
}
