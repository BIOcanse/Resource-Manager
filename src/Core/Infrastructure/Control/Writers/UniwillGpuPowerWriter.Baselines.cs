using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite;

namespace ResourceManager.App.Infrastructure.Control.Writers;

public sealed partial class UniwillGpuPowerWriter
{
    private readonly object baselineGate = new();
    private readonly string? baselinePath;
    private Dictionary<string, BudgetBaseline>? baselines;
    private static readonly JsonSerializerOptions BaselineJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record BudgetBaseline(
        [property: JsonRequired] byte Ctgp,
        [property: JsonRequired] byte DynamicBoost,
        [property: JsonRequired] bool DynamicBoostEnabled);

    private void LoadBaselines()
    {
        if (baselines is not null) return;
        if (baselinePath is null || !File.Exists(baselinePath))
        {
            baselines = [];
            return;
        }
        if (new FileInfo(baselinePath).Length > 65_536) throw new InvalidDataException("GPU baseline is too large.");
        var stored = JsonSerializer.Deserialize<Dictionary<string, BudgetBaseline>>(
            File.ReadAllText(baselinePath), BaselineJson)
            ?? throw new InvalidDataException("GPU baseline is empty.");
        if (stored.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null))
            throw new InvalidDataException("GPU baseline has an invalid entry.");
        baselines = stored;
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Uniwill GPU power control takeover requested")]
    private bool CaptureBaseline(ControlObject target, byte enable)
    {
        LoadBaselines();
        if (baselines!.ContainsKey(target.Id)) return true;
        if (bridge.Read(UniwillEcRegisters.CtgpOffset) is not { } ctgp
            || bridge.Read(UniwillEcRegisters.DynamicBoostOffset) is not { } dynamicBoost) return false;
        var next = new Dictionary<string, BudgetBaseline>(baselines)
        {
            [target.Id] = new(ctgp, dynamicBoost, (enable & UniwillEcRegisters.CtgpDbEnableDynamicBoostBit) != 0)
        };
        SaveBaselines(next);
        return true;
    }

    private void RemoveBaseline(ControlObject target)
    {
        LoadBaselines();
        if (!baselines!.ContainsKey(target.Id)) return;
        var next = new Dictionary<string, BudgetBaseline>(baselines);
        next.Remove(target.Id);
        SaveBaselines(next);
    }

    private void SaveBaselines(Dictionary<string, BudgetBaseline> next)
    {
        // Only the injected hardware test constructor uses an in-memory baseline.
        if (baselinePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            var temporary = baselinePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next, BaselineJson);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, baselinePath, overwrite: true);
        }
        baselines = next;
    }

    private static bool IsBaselineError(Exception error)
        => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
}
