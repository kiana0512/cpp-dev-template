using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VhSenderGui.Services;

public sealed class AiFramesPlaybackService
{
    public async Task PlayAsync(
        SenderSession session,
        string framesPath,
        bool useTimestamp,
        double fps,
        Action<int, int, ulong?, double?> onProgress,
        CancellationToken ct)
    {
        var total = 0;
        await foreach (var _ in ReadNonEmptyLinesAsync(framesPath, ct))
            total++;
        if (total == 0)
            throw new InvalidOperationException("frames.jsonl is empty: " + framesPath);

        var sw = Stopwatch.StartNew();
        var lastProgress = Stopwatch.StartNew();
        var i = 0;
        await foreach (var lineRaw in ReadNonEmptyLinesAsync(framesPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            var line = lineRaw.Trim();
            ulong? seq = null;
            double? timeSec = null;
            var timestampMs = (long)Math.Round(i * 1000.0 / Math.Max(1.0, fps));
            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("sequence_id", out var seqEl) && seqEl.TryGetUInt64(out var s)) seq = s;
                if (root.TryGetProperty("timestamp_ms", out var tsEl) && tsEl.TryGetInt64(out var t)) timestampMs = t;
                if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("time_sec", out var timeEl) && timeEl.TryGetDouble(out var ts)) timeSec = ts;
            }

            var targetSec = useTimestamp ? timestampMs / 1000.0 : i / Math.Max(1.0, fps);
            var delay = TimeSpan.FromSeconds(targetSec) - sw.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            var result = await session.SendRawJsonLineAsync(line, ct);
            if (!result.Success)
                throw new InvalidOperationException(result.ErrorMessage);

            if (i == 0 || i == total - 1 || (i + 1) % 30 == 0 || lastProgress.Elapsed.TotalSeconds >= 0.5)
            {
                onProgress(i + 1, total, seq, timeSec ?? targetSec);
                lastProgress.Restart();
            }
            i++;
        }
        var avgFps = total / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        onProgress(total, total, null, avgFps);
    }

    private static async IAsyncEnumerable<string> ReadNonEmptyLinesAsync(string framesPath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = new FileStream(framesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            yield return line;
        }
    }
}
