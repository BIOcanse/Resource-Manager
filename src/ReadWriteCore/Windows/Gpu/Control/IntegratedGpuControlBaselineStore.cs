using System.Text.Json;

namespace ResourceManager.ReadWrite.Windows.Gpu.Control;

/// <summary>按控制对象身份保存首次有限基线。旧标量没有归属，不能作为恢复值。</summary>
public sealed class IntegratedGpuControlBaselineStore(string path)
{
    private readonly object gate = new();
    private Dictionary<string, double>? baselines;

    public double? Capture(string targetId, double? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        lock (gate)
        {
            if (baselines is null)
            {
                if (!File.Exists(path)) baselines = new(StringComparer.Ordinal);
                else
                {
                    if (new FileInfo(path).Length > 65_536) return null;
                    try
                    {
                        var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
                        if (saved is null || saved.Count > 128 || saved.Any(entry =>
                            string.IsNullOrWhiteSpace(entry.Key) || !double.IsFinite(entry.Value))) return null;
                        baselines = new(saved, StringComparer.Ordinal);
                    }
                    catch (JsonException) { return null; }
                }
            }
            if (baselines.TryGetValue(targetId, out var baseline)) return baseline;
            if (current is not { } value || !double.IsFinite(value)) return null;
            if (baselines.Count >= 128) return null;
            var next = new Dictionary<string, double>(baselines, StringComparer.Ordinal) { [targetId] = value };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            baselines = next;
            return value;
        }
    }
}
