using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Raw store snapshots avoid re-entering the shared mutation gate during deletion.</summary>
public sealed class GpuVfCurveReferences(IControlPresetStore presets, IControlDesiredStateStore desired,
    GpuVfCurveAppliedState applied) : IGpuVfCurveReferences
{
    public async Task<IReadOnlyList<GpuVfCurveReference>> GetAsync(string curveId, CancellationToken cancellationToken)
    {
        var catalog = await presets.LoadAsync(cancellationToken).ConfigureAwait(false);
        var state = await desired.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = catalog.Presets.Where(preset => preset.Settings.Any(setting => setting.GpuVfCurvePresetId == curveId))
            .Select(preset => new GpuVfCurveReference("preset", preset.Id, preset.ObjectId, preset.Name)).ToList();
        foreach (var target in state.Objects.Where(target => target.Settings.Any(setting => setting.GpuVfCurvePresetId == curveId)))
            result.Add(new("desired", target.ObjectId, target.ObjectId));
        foreach (var target in state.PendingReleases.Where(target => target.Settings.Any(setting => setting.GpuVfCurvePresetId == curveId)))
            result.Add(new("pending-release", target.ObjectId, target.ObjectId));
        result.AddRange(applied.References(curveId));
        return result;
    }
}
