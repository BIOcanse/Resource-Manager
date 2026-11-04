using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Reference acceptance is serialized with library deletion. Client-supplied execution data is never trusted.</summary>
public sealed class GpuVfCurveControlInputs(IGpuVfCurvePresets curves, GpuVfCurveMigration migration)
{
    internal async Task<IReadOnlyList<ControlSetting>> PrepareAsync(string objectId,
        IReadOnlyList<ControlSetting> settings, bool acceptedExecution, CancellationToken token)
    {
        settings = await migration.SettingsAsync(objectId, settings, token).ConfigureAwait(false);
        if (settings.Count(setting => setting.CapabilityId == GpuVfCurveCapabilities.Core) > 1)
            throw new GpuVfCurveRequestException(new("gpu-vf-curve-duplicate-setting"));
        var result = new List<ControlSetting>(settings.Count);
        foreach (var setting in settings)
        {
            if (setting.CapabilityId == GpuVfCurveScalarAdapter.LegacyCapabilityId)
                throw new GpuVfCurveRequestException(new("gpu-vf-curve-legacy-migration-unavailable"));
            if (setting.CapabilityId != GpuVfCurveCapabilities.Core)
            {
                if (setting.GpuVfCurvePresetId is not null || setting.GpuVfCurveExecution is not null)
                    throw new GpuVfCurveRequestException(new("gpu-vf-curve-value-shape-invalid"));
                result.Add(setting);
                continue;
            }
            if (string.IsNullOrWhiteSpace(setting.GpuVfCurvePresetId) || setting.Number is not null
                || setting.Toggle is not null || setting.Curve is not null || setting.CurveExecution is not null || setting.Unit is not null)
                throw new GpuVfCurveRequestException(new("gpu-vf-curve-value-shape-invalid"));
            var execution = await curves.ResolveAsync(objectId, setting.GpuVfCurvePresetId, token).ConfigureAwait(false);
            result.Add(new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: setting.GpuVfCurvePresetId,
                GpuVfCurveExecution: acceptedExecution ? execution : null));
        }
        return result;
    }
}

internal sealed class GpuVfCurveMutationScope(SemaphoreSlim semaphore) : IDisposable
{
    internal static async Task<IDisposable?> EnterAsync(GpuVfCurveMutationGate? mutation, CancellationToken token)
    {
        if (mutation is null) return null;
        await mutation.Semaphore.WaitAsync(token).ConfigureAwait(false);
        return new GpuVfCurveMutationScope(mutation.Semaphore);
    }
    public void Dispose() => semaphore.Release();
}
