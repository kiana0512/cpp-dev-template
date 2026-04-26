namespace VhSenderGui.Services;

/// <summary>Port of vh::PacketValidator::validate.</summary>
public static class PacketValidatorService
{
    public static IReadOnlyList<string> Validate(ExpressionFrameDto frame)
    {
        var errors = new List<string>();

        if (frame.Type != "expression_frame")
            errors.Add("type must be expression_frame");

        if (string.IsNullOrEmpty(frame.Version))
            errors.Add("version must not be empty");

        if (string.IsNullOrEmpty(frame.CharacterId))
            errors.Add("character_id must not be empty");

        if (!InUnitRange(frame.Audio.Rms))
            errors.Add("audio.rms must be in [0, 1]");

        if (!InUnitRange(frame.Emotion.Confidence))
            errors.Add("emotion.confidence must be in [0, 1]");

        foreach (var kv in frame.Blendshapes)
        {
            if (string.IsNullOrEmpty(kv.Key))
            {
                errors.Add("blendshape key must not be empty");
                continue;
            }
            if (!InUnitRange(kv.Value))
                errors.Add($"blendshape '{kv.Key}' must be in [0, 1]");
        }

        if (!InHeadRange(frame.HeadPose.Pitch))
            errors.Add("head_pose.pitch must be in [-90, 90]");
        if (!InHeadRange(frame.HeadPose.Yaw))
            errors.Add("head_pose.yaw must be in [-90, 90]");
        if (!InHeadRange(frame.HeadPose.Roll))
            errors.Add("head_pose.roll must be in [-90, 90]");

        return errors;
    }

    private static bool InUnitRange(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0.0 && v <= 1.0;

    private static bool InHeadRange(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) && v >= -90.0 && v <= 90.0;
}
