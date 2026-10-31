using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Confirmed curve inputs remain referenced until a confirmed replacement or release.</summary>
public sealed class GpuVfCurveAppliedState
{
    private readonly object gate = new();
    private readonly Dictionary<string, ControlSetting> settings = new(StringComparer.Ordinal);

    internal void Forget(string objectId)
    {
        lock (gate) settings.Remove(objectId);
    }

    internal IReadOnlyList<GpuVfCurveReference> References(string curveId)
    {
        lock (gate)
            return settings.Where(entry => entry.Value.GpuVfCurvePresetId == curveId)
                .Select(entry => new GpuVfCurveReference("applied", entry.Key, entry.Key)).ToArray();
    }

    internal void Record(ControlDesiredState desired, ControlApplyReport writes, ControlApplyReport releases)
    {
        lock (gate)
        {
            foreach (var outcome in releases.Outcomes.Where(entry => entry.CapabilityId == GpuVfCurveCapabilities.Core
                         && entry.Status == ControlApplyStatuses.Applied))
                settings.Remove(outcome.ObjectId);
            foreach (var outcome in writes.Outcomes.Where(entry => entry.CapabilityId == GpuVfCurveCapabilities.Core
                         && entry.Status == ControlApplyStatuses.Applied))
            {
                var accepted = desired.ForObject(outcome.ObjectId)?.Settings.FirstOrDefault(entry =>
                    entry.CapabilityId == GpuVfCurveCapabilities.Core && entry.GpuVfCurveExecution is not null);
                if (accepted is not null) settings[outcome.ObjectId] = accepted;
            }
        }
    }
}
