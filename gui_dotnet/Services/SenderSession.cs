using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

/// <summary>Mirrors vh::SenderService pipeline (build → validate → serialize → send). GUI-only transport in C#.</summary>
public sealed class SenderSession : IDisposable
{
    public sealed record SessionOptions(
        string Mode = "tcp",
        string Host = "127.0.0.1",
        int Port = 7001,
        string OutputPath = "outputs/frames.jsonl",
        string CharacterId = "miku_yyb_001",
        ulong StartSeq = 1);

    public sealed record SendResult(
        bool Success,
        string JsonPayload,
        string ErrorMessage,
        ulong Seq,
        int PayloadBytes);

    public delegate void LogHandler(string level, string message);

    private readonly SessionOptions _opts;
    private readonly LogHandler? _log;
    private TcpClient? _tcp;
    private NetworkStream? _tcpStream;
    private StreamWriter? _fileWriter;
    private ulong _seq;
    private bool _connected;
    private ulong _lastSentSeq;
    private string _lastJson = "";
    private string _lastError = "";

    public SenderSession(SessionOptions opts, LogHandler? log = null)
    {
        _opts = opts;
        _log = log;
        _seq = opts.StartSeq;
        Log("INFO", $"SenderSession created: mode={opts.Mode} target={opts.Host}:{opts.Port} character={opts.CharacterId} start_seq={opts.StartSeq}");
    }

    public bool IsConnected => _connected;
    public ulong LastSentSeq => _lastSentSeq;
    public string LastJson => _lastJson;
    public string LastError => _lastError;
    public SessionOptions Config => _opts;

    private void Log(string level, string msg) => _log?.Invoke(level, msg);

    private static bool IsValidPhoneme(string p) =>
        p is "rest" or "a" or "i" or "u" or "e" or "o";

    public bool Connect()
    {
        if (_connected)
        {
            Log("WARN", "Already connected — ignoring redundant connect()");
            return true;
        }

        Log("INFO", $"Connecting: mode={_opts.Mode} target={_opts.Host}:{_opts.Port}");

        try
        {
            switch (_opts.Mode)
            {
                case "tcp":
                    _tcp = new TcpClient();
                    _tcp.Connect(_opts.Host, _opts.Port);
                    _tcpStream = _tcp.GetStream();
                    break;
                case "file":
                    var outPath = Path.IsPathRooted(_opts.OutputPath)
                        ? _opts.OutputPath
                        : Path.Combine(RepoPaths.FindRepoRoot(), _opts.OutputPath);
                    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    _fileWriter = new StreamWriter(outPath, append: true, Encoding.UTF8);
                    break;
                case "stdout":
                    break;
                default:
                    _lastError = $"Unknown transport mode: \"{_opts.Mode}\". Valid: tcp | file | stdout";
                    Log("ERROR", _lastError);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Transport connect() failed: {ex.Message}";
            Log("ERROR", _lastError);
            CleanupTransport();
            return false;
        }

        _connected = true;
        Log("INFO", $"Connected successfully to {_opts.Host}:{_opts.Port} (mode={_opts.Mode})");
        return true;
    }

    public void Disconnect()
    {
        CleanupTransport();
        if (_connected)
            Log("INFO", $"Disconnected from {_opts.Host}:{_opts.Port}");
        _connected = false;
    }

    private void CleanupTransport()
    {
        _tcpStream?.Dispose();
        _tcpStream = null;
        _tcp?.Close();
        _tcp = null;
        _fileWriter?.Dispose();
        _fileWriter = null;
    }

    public SendResult SendFrame(MapperInput inputIn)
    {
        _lastError = "";
        var result = new SendResult(false, "", "", _seq, 0);

        if (!_connected)
        {
            _lastError = "Not connected — call Connect() before SendFrame().";
            Log("ERROR", _lastError);
            return result with { ErrorMessage = _lastError };
        }

        var input = CloneInput(inputIn);
        input.SequenceId = _seq;
        input.CharacterId = _opts.CharacterId;

        if (!IsValidPhoneme(input.PhonemeHint))
        {
            Log("WARN", $"Invalid phoneme_hint='{input.PhonemeHint}' — falling back to 'rest'.");
            input.PhonemeHint = "rest";
        }

        if (string.IsNullOrEmpty(input.Text))
            Log("WARN", $"meta.text is empty (seq={_seq})");

        ExpressionFrameDto frame;
        try
        {
            frame = ExpressionMapperService.BuildFrame(input);
        }
        catch (Exception ex)
        {
            _lastError = $"ExpressionMapperService.BuildFrame threw: {ex.Message} (seq={_seq})";
            Log("ERROR", _lastError);
            return result with { ErrorMessage = _lastError };
        }

        var errors = PacketValidatorService.Validate(frame);
        if (errors.Count > 0)
        {
            _lastError = "PacketValidator failed (seq=" + _seq + "): " + string.Join(" | ", errors);
            Log("ERROR", _lastError);
            return result with { ErrorMessage = _lastError };
        }

        string payload;
        try
        {
            payload = FrameJsonSerializer.ToJsonLine(frame);
        }
        catch (Exception ex)
        {
            _lastError = $"FrameJsonSerializer threw: {ex.Message} (seq={_seq})";
            Log("ERROR", _lastError);
            return result with { ErrorMessage = _lastError };
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(payload) + 1;
        Log("JSON", payload);
        Log("FRAME",
            $"seq={_seq} emotion={frame.Emotion.Label} phoneme={frame.Audio.PhonemeHint} rms={frame.Audio.Rms} pitch={frame.HeadPose.Pitch} yaw={frame.HeadPose.Yaw} bytes={payloadBytes}");

        if (!SendLine(payload))
        {
            _lastError = $"send failed at seq={_seq}. Connection may have been dropped.";
            Log("ERROR", _lastError);
            _connected = false;
            return result with { JsonPayload = payload, ErrorMessage = _lastError };
        }

        _lastJson = payload;
        _lastSentSeq = _seq;
        var sentSeq = _seq;
        ++_seq;
        Log("INFO", $"Sent seq={sentSeq} emotion={frame.Emotion.Label} bytes={payloadBytes} -> {_opts.Host}:{_opts.Port}");
        return new SendResult(true, payload, "", sentSeq, payloadBytes);
    }

    /// <summary>Send a full frame JSON (e.g. configs/sample_frame.json) without re-mapping. Does not advance <see cref="_seq"/>.</summary>
    public SendResult SendRawJson(string rawJson)
    {
        _lastError = "";
        var fail = new SendResult(false, "", "", _seq, 0);

        if (!_connected)
        {
            _lastError = "Not connected — call Connect() before SendRawJson().";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        if (!FrameJsonSerializer.TryNormalizeLine(rawJson, out var payload, out var normErr))
        {
            _lastError = normErr ?? "Invalid JSON";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        ExpressionFrameDto? frame;
        try
        {
            frame = JsonSerializer.Deserialize<ExpressionFrameDto>(payload, FrameJsonSerializer.DeserializeOptions);
        }
        catch (Exception ex)
        {
            _lastError = $"Deserialize frame failed: {ex.Message}";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        if (frame == null)
        {
            _lastError = "Deserialize returned null";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        var errors = PacketValidatorService.Validate(frame);
        if (errors.Count > 0)
        {
            _lastError = "PacketValidator failed (raw): " + string.Join(" | ", errors);
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError, JsonPayload = payload };
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(payload) + 1;
        Log("JSON", payload);
        Log("FRAME",
            $"raw seq={frame.SequenceId} emotion={frame.Emotion.Label} phoneme={frame.Audio.PhonemeHint} bytes={payloadBytes}");

        if (!SendLine(payload))
        {
            _lastError = "send failed (raw JSON). Connection may have been dropped.";
            Log("ERROR", _lastError);
            _connected = false;
            return fail with { JsonPayload = payload, ErrorMessage = _lastError };
        }

        _lastJson = payload;
        _lastSentSeq = frame.SequenceId;
        Log("INFO", $"Sent raw JSON seq={frame.SequenceId} bytes={payloadBytes} -> {_opts.Host}:{_opts.Port}");
        return new SendResult(true, payload, "", frame.SequenceId, payloadBytes);
    }

    public async Task<SendResult> SendRawJsonLineAsync(string jsonLine, CancellationToken ct)
    {
        _lastError = "";
        var fail = new SendResult(false, "", "", _seq, 0);

        if (!_connected)
        {
            _lastError = "Not connected; call Connect() before SendRawJsonLineAsync().";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        var raw = (jsonLine ?? "").Trim();
        if (raw.Contains('\n') || raw.Contains('\r'))
            raw = string.Join("", raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        if (!FrameJsonSerializer.TryNormalizeLine(raw, out var payload, out var normErr))
        {
            _lastError = normErr ?? "Invalid JSON";
            Log("ERROR", _lastError);
            return fail with { ErrorMessage = _lastError };
        }

        ulong seq = _seq;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("sequence_id", out var seqEl) && seqEl.TryGetUInt64(out var parsed))
                seq = parsed;
        }
        catch
        {
            /* normalized JSON already parsed; keep fallback seq */
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(payload) + 1;
        if (!await SendLineAsync(payload, ct))
        {
            _lastError = $"send failed at raw seq={seq}. Connection may have been dropped.";
            Log("ERROR", _lastError);
            _connected = false;
            return fail with { JsonPayload = payload, ErrorMessage = _lastError };
        }

        _lastJson = payload;
        _lastSentSeq = seq;
        Log("JSON", payload);
        return new SendResult(true, payload, "", seq, payloadBytes);
    }

    private bool SendLine(string payload)
    {
        var line = Encoding.UTF8.GetBytes(payload + "\n");
        try
        {
            switch (_opts.Mode)
            {
                case "tcp":
                    if (_tcpStream == null) return false;
                    _tcpStream.Write(line, 0, line.Length);
                    _tcpStream.Flush();
                    return true;
                case "file":
                    if (_fileWriter == null) return false;
                    _fileWriter.WriteLine(payload);
                    _fileWriter.Flush();
                    return true;
                case "stdout":
                    Console.WriteLine(payload);
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private async Task<bool> SendLineAsync(string payload, CancellationToken ct)
    {
        var line = Encoding.UTF8.GetBytes(payload + "\n");
        try
        {
            switch (_opts.Mode)
            {
                case "tcp":
                    if (_tcpStream == null) return false;
                    await _tcpStream.WriteAsync(line, ct);
                    await _tcpStream.FlushAsync(ct);
                    return true;
                case "file":
                    if (_fileWriter == null) return false;
                    await _fileWriter.WriteLineAsync(payload.AsMemory(), ct);
                    await _fileWriter.FlushAsync(ct);
                    return true;
                case "stdout":
                    Console.WriteLine(payload);
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static MapperInput CloneInput(MapperInput m) => new()
    {
        Text = m.Text,
        EmotionLabel = m.EmotionLabel,
        EmotionConfidence = m.EmotionConfidence,
        Rms = m.Rms,
        PhonemeHint = m.PhonemeHint,
        CharacterId = m.CharacterId,
        SequenceId = m.SequenceId,
        HeadPosePitch = m.HeadPosePitch,
        HeadPoseYaw = m.HeadPoseYaw,
        HeadPoseRoll = m.HeadPoseRoll,
    };

    public void Dispose()
    {
        Disconnect();
    }

    /// <summary>Connect + close, no payload — TCP reachability only.</summary>
    public static bool ProbeTcp(string host, int port, out string error)
    {
        error = "";
        try
        {
            using var c = new TcpClient();
            c.Connect(host, port);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
