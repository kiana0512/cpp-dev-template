using System.Text.Encodings.Web;
using System.Text.Json;

namespace VhSenderGui.Services;

public static class FrameJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Options for deserializing snake_case JSON into DTOs (property names via attributes).</summary>
    public static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Single-line JSON matching C++ JsonProtocol::toJsonString(frame, false).</summary>
    public static string ToJsonLine(ExpressionFrameDto frame) =>
        JsonSerializer.Serialize(frame, Options);

    /// <summary>Parse arbitrary JSON text and emit one compact line (for TCP / file replay).</summary>
    public static bool TryNormalizeLine(string raw, out string line, out string? error)
    {
        line = "";
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            line = JsonSerializer.Serialize(doc.RootElement, Options);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
