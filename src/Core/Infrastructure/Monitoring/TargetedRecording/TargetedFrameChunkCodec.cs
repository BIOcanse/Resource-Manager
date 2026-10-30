using System.Buffers;
using System.Text;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Frames;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>Compact frame chunk encoding (about 10 bytes per present instead of about 150 for the
/// object-per-present JSON array). One stream per (process, start key, source, swap chain):
/// {"v":2,"s":[{"p":pid,"k":startKey,"r":source,"c":swapChain,"t":firstEndedTicks,"d":[dtUs,durUs,...]}]}.
/// dtUs is the end time minus the previous end time in the stream (0 for the first present); durUs is the
/// frame time. Both are whole microseconds. Chunks written before this encoding are plain arrays and are
/// still read.</summary>
public static class TargetedFrameChunkCodec
{
    public const int Version = 2;

    public static string Encode(IReadOnlyList<FrameIntervalSample> intervals)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteStartArray("s");
            foreach (var stream in intervals.GroupBy(static item => (item.ProcessId, item.ProcessStartKey, item.Source, item.SwapChain)))
            {
                writer.WriteStartObject();
                writer.WriteNumber("p", stream.Key.ProcessId);
                writer.WriteNumber("k", stream.Key.ProcessStartKey);
                writer.WriteNumber("r", (int)stream.Key.Source);
                writer.WriteNumber("c", stream.Key.SwapChain);
                var ordered = stream.OrderBy(static item => item.EndedAt).ToArray();
                writer.WriteNumber("t", ordered[0].EndedAt.UtcTicks);
                writer.WriteStartArray("d");
                var previous = ordered[0].EndedAt.UtcTicks;
                foreach (var item in ordered)
                {
                    var ticks = item.EndedAt.UtcTicks;
                    writer.WriteNumberValue((ticks - previous) / TimeSpan.TicksPerMicrosecond);
                    writer.WriteNumberValue((long)Math.Round(item.DurationMs * 1000d));
                    // Rebuild from the rounded value so decoded end times do not drift.
                    previous += (ticks - previous) / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond;
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static IReadOnlyList<FrameIntervalSample> Decode(string json)
    {
        if (json.Length == 0) return [];
        if (json[0] == '[') return JsonSerializer.Deserialize<FrameIntervalSample[]>(json) ?? [];
        using var document = JsonDocument.Parse(json);
        var result = new List<FrameIntervalSample>();
        foreach (var stream in document.RootElement.GetProperty("s").EnumerateArray())
        {
            var processId = stream.GetProperty("p").GetInt32();
            var startKey = stream.GetProperty("k").GetInt64();
            var source = (FramePresentSource)stream.GetProperty("r").GetInt32();
            var swapChain = stream.GetProperty("c").GetUInt64();
            var ticks = stream.GetProperty("t").GetInt64();
            var values = stream.GetProperty("d").EnumerateArray();
            while (values.MoveNext())
            {
                ticks += values.Current.GetInt64() * TimeSpan.TicksPerMicrosecond;
                if (!values.MoveNext()) break;
                var durationMs = values.Current.GetInt64() / 1000d;
                result.Add(new FrameIntervalSample(processId, startKey, source, swapChain,
                    new DateTimeOffset(ticks, TimeSpan.Zero), durationMs));
            }
        }
        result.Sort(static (left, right) => left.EndedAt.CompareTo(right.EndedAt));
        return result;
    }
}
