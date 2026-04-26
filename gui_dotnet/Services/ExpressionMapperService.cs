using System.Text.Json.Serialization;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

/// <summary>Port of vh::ExpressionMapper::buildFrame — keep in sync with src/expression_mapper.cpp.</summary>
public static class ExpressionMapperService
{
    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private static ulong NowMs() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Dictionary<string, double> MakeNeutral() => new()
    {
        ["jawOpen"] = 0.10, ["mouthSmile"] = 0.00, ["mouthFrown"] = 0.00,
        ["eyeBlinkLeft"] = 0.03, ["eyeBlinkRight"] = 0.03,
        ["eyeSquintLeft"] = 0.00, ["eyeSquintRight"] = 0.00,
        ["eyeWideLeft"] = 0.00, ["eyeWideRight"] = 0.00,
        ["browInnerUp"] = 0.00, ["browDown"] = 0.00,
        ["browOuterUpLeft"] = 0.00, ["browOuterUpRight"] = 0.00,
        ["noseSneerLeft"] = 0.00, ["noseSneerRight"] = 0.00,
    };

    private static Dictionary<string, double> MakeHappy() => new()
    {
        ["jawOpen"] = 0.20, ["mouthSmile"] = 0.78, ["mouthFrown"] = 0.00,
        ["eyeBlinkLeft"] = 0.05, ["eyeBlinkRight"] = 0.05,
        ["eyeSquintLeft"] = 0.32, ["eyeSquintRight"] = 0.32,
        ["eyeWideLeft"] = 0.00, ["eyeWideRight"] = 0.00,
        ["browInnerUp"] = 0.18, ["browDown"] = 0.00,
        ["browOuterUpLeft"] = 0.10, ["browOuterUpRight"] = 0.10,
        ["noseSneerLeft"] = 0.00, ["noseSneerRight"] = 0.00,
    };

    private static Dictionary<string, double> MakeSad() => new()
    {
        ["jawOpen"] = 0.12, ["mouthSmile"] = 0.00, ["mouthFrown"] = 0.58,
        ["eyeBlinkLeft"] = 0.10, ["eyeBlinkRight"] = 0.10,
        ["eyeSquintLeft"] = 0.08, ["eyeSquintRight"] = 0.08,
        ["eyeWideLeft"] = 0.00, ["eyeWideRight"] = 0.00,
        ["browInnerUp"] = 0.48, ["browDown"] = 0.08,
        ["browOuterUpLeft"] = 0.00, ["browOuterUpRight"] = 0.00,
        ["noseSneerLeft"] = 0.00, ["noseSneerRight"] = 0.00,
    };

    private static Dictionary<string, double> MakeAngry() => new()
    {
        ["jawOpen"] = 0.18, ["mouthSmile"] = 0.00, ["mouthFrown"] = 0.30,
        ["eyeBlinkLeft"] = 0.02, ["eyeBlinkRight"] = 0.02,
        ["eyeSquintLeft"] = 0.22, ["eyeSquintRight"] = 0.22,
        ["eyeWideLeft"] = 0.00, ["eyeWideRight"] = 0.00,
        ["browInnerUp"] = 0.00, ["browDown"] = 0.72,
        ["browOuterUpLeft"] = 0.00, ["browOuterUpRight"] = 0.00,
        ["noseSneerLeft"] = 0.55, ["noseSneerRight"] = 0.55,
    };

    private static Dictionary<string, double> MakeSurprised() => new()
    {
        ["jawOpen"] = 0.55, ["mouthSmile"] = 0.00, ["mouthFrown"] = 0.00,
        ["eyeBlinkLeft"] = 0.00, ["eyeBlinkRight"] = 0.00,
        ["eyeSquintLeft"] = 0.00, ["eyeSquintRight"] = 0.00,
        ["eyeWideLeft"] = 0.82, ["eyeWideRight"] = 0.82,
        ["browInnerUp"] = 0.38, ["browDown"] = 0.00,
        ["browOuterUpLeft"] = 0.50, ["browOuterUpRight"] = 0.50,
        ["noseSneerLeft"] = 0.00, ["noseSneerRight"] = 0.00,
    };

    private static Dictionary<string, double> EmotionPreset(string label) => label switch
    {
        "happy" => MakeHappy(),
        "sad" => MakeSad(),
        "angry" => MakeAngry(),
        "surprised" => MakeSurprised(),
        _ => MakeNeutral(),
    };

    private static void ApplyAudioAndPhoneme(Dictionary<string, double> bs, double rms, string phoneme)
    {
        var jawFromRms = Clamp01(0.12 + rms * 0.88);
        bs["jawOpen"] = Math.Max(bs["jawOpen"], jawFromRms);

        switch (phoneme)
        {
            case "a":
                bs["jawOpen"] = Clamp01(bs["jawOpen"] + 0.10);
                break;
            case "o":
                bs["jawOpen"] = Clamp01(bs["jawOpen"] + 0.06);
                break;
            case "i":
                bs["mouthSmile"] = Clamp01(bs["mouthSmile"] + 0.10);
                bs["eyeSquintLeft"] = Clamp01(bs["eyeSquintLeft"] + 0.05);
                bs["eyeSquintRight"] = Clamp01(bs["eyeSquintRight"] + 0.05);
                break;
            case "e":
                bs["jawOpen"] = Clamp01(bs["jawOpen"] + 0.04);
                bs["mouthSmile"] = Clamp01(bs["mouthSmile"] + 0.04);
                break;
            case "u":
                bs["mouthFrown"] = Clamp01(bs["mouthFrown"] + 0.05);
                break;
        }
    }

    private static double Clamp90(double v) => Math.Clamp(v, -90.0, 90.0);

    public static ExpressionFrameDto BuildFrame(MapperInput input)
    {
        var frame = new ExpressionFrameDto
        {
            TimestampMs = NowMs(),
            CharacterId = input.CharacterId,
            SequenceId = input.SequenceId,
            Audio = new AudioDto
            {
                Playing = true,
                Rms = Clamp01(input.Rms),
                PhonemeHint = input.PhonemeHint,
            },
            Emotion = new EmotionDto
            {
                Label = input.EmotionLabel,
                Confidence = Clamp01(input.EmotionConfidence),
            },
            Blendshapes = EmotionPreset(input.EmotionLabel),
            Meta = new MetaDto { Source = "expression_mapper", Text = input.Text },
        };

        ApplyAudioAndPhoneme(frame.Blendshapes, frame.Audio.Rms, input.PhonemeHint);

        if (input.EmotionLabel == "happy")
            frame.HeadPose.Yaw = -3.0;
        else if (input.EmotionLabel == "sad")
            frame.HeadPose.Pitch = -5.0;
        else if (input.EmotionLabel == "surprised")
            frame.HeadPose.Pitch = 4.0;
        else if (input.EmotionLabel == "angry")
        {
            frame.HeadPose.Yaw = 2.0;
            frame.HeadPose.Pitch = -1.5;
        }

        frame.HeadPose.Pitch = Clamp90(frame.HeadPose.Pitch + input.HeadPosePitch);
        frame.HeadPose.Yaw = Clamp90(frame.HeadPose.Yaw + input.HeadPoseYaw);
        frame.HeadPose.Roll = Clamp90(frame.HeadPose.Roll + input.HeadPoseRoll);

        return frame;
    }
}

// DTOs mirror vh::ExpressionFrame / JsonProtocol::toJson field names
public sealed class ExpressionFrameDto
{
    [JsonPropertyName("type")] public string Type { get; set; } = "expression_frame";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("timestamp_ms")] public ulong TimestampMs { get; set; }
    [JsonPropertyName("character_id")] public string CharacterId { get; set; } = "miku_yyb_001";
    [JsonPropertyName("sequence_id")] public ulong SequenceId { get; set; }
    [JsonPropertyName("audio")] public AudioDto Audio { get; set; } = new();
    [JsonPropertyName("emotion")] public EmotionDto Emotion { get; set; } = new();
    [JsonPropertyName("blendshapes")] public Dictionary<string, double> Blendshapes { get; set; } = new();
    [JsonPropertyName("head_pose")] public HeadPoseDto HeadPose { get; set; } = new();
    [JsonPropertyName("meta")] public MetaDto Meta { get; set; } = new();
}

public sealed class AudioDto
{
    [JsonPropertyName("playing")] public bool Playing { get; set; }
    [JsonPropertyName("rms")] public double Rms { get; set; }
    [JsonPropertyName("phoneme_hint")] public string PhonemeHint { get; set; } = "rest";
}

public sealed class EmotionDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "neutral";
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 1.0;
}

public sealed class HeadPoseDto
{
    [JsonPropertyName("pitch")] public double Pitch { get; set; }
    [JsonPropertyName("yaw")] public double Yaw { get; set; }
    [JsonPropertyName("roll")] public double Roll { get; set; }
}

public sealed class MetaDto
{
    [JsonPropertyName("source")] public string Source { get; set; } = "demo";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}
