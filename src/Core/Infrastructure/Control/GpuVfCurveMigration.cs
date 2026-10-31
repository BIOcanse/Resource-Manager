using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// Visible, idempotent migration of pre-curve persisted offsets. Remove this compatibility
/// boundary when every supported upgrade starts after the first curve-capability release.
/// Curves are saved first; a crash before reference persistence is safe because EnsureUniform deduplicates.
/// </summary>
public sealed class GpuVfCurveMigration(IControlPresetStore presets, IControlDesiredStateStore desired,
    IGpuVfCurvePresets curves, GpuVfCurveMutationGate mutation,
    ILogger<GpuVfCurveMigration>? logger = null, StartupCapabilitySet? capabilities = null)
{
    private bool CanMutate => capabilities is null || capabilities.Allows(StartupCapability.MutablePersistence);
    public async Task MigrateAsync(CancellationToken token)
    {
        if (!CanMutate) return;
        await mutation.Semaphore.WaitAsync(token).ConfigureAwait(false);
        try { await MigrateHeldAsync(token).ConfigureAwait(false); }
        finally { mutation.Semaphore.Release(); }
    }

    internal async Task MigrateHeldAsync(CancellationToken token)
    {
        if (!CanMutate) return;
        var catalog = await presets.LoadAsync(token).ConfigureAwait(false);
        var migrated = new List<ControlPreset>(catalog.Presets.Count);
        var changed = false;
        foreach (var preset in catalog.Presets)
        {
            var settings = await SettingsAsync(preset.ObjectId, preset.Settings, token).ConfigureAwait(false);
            changed |= !ReferenceEquals(settings, preset.Settings);
            migrated.Add(preset with { Settings = settings });
        }
        if (changed) await presets.SaveAsync(new(migrated), token).ConfigureAwait(false);
        var state = await desired.LoadAsync(token).ConfigureAwait(false);
        var objects = new List<ControlObjectDesiredState>(state.Objects.Count);
        changed = false;
        foreach (var target in state.Objects)
        {
            var settings = await SettingsAsync(target.ObjectId, target.Settings, token).ConfigureAwait(false);
            changed |= !ReferenceEquals(settings, target.Settings);
            if (settings.Any(setting => setting.CapabilityId == GpuVfCurveCapabilities.Core && setting.GpuVfCurveExecution is null))
            {
                var accepted = new List<ControlSetting>(settings.Count);
                foreach (var setting in settings)
                {
                    if (setting.CapabilityId == GpuVfCurveCapabilities.Core && setting.GpuVfCurveExecution is null
                        && setting.GpuVfCurvePresetId is { } id)
                    {
                        try
                        {
                            accepted.Add(setting with { GpuVfCurveExecution = await curves.ResolveAsync(target.ObjectId, id, token).ConfigureAwait(false) });
                            changed = true;
                        }
                        catch (GpuVfCurveRequestException error)
                        {
                            logger?.LogWarning("GPU V/F accepted input migration pending for {ObjectId}: {Reason}", target.ObjectId, error.Reason.Code);
                            accepted.Add(setting);
                        }
                    }
                    else accepted.Add(setting);
                }
                settings = accepted;
            }
            objects.Add(target with { Settings = settings });
        }
        if (changed) await desired.SaveAsync(state with { Objects = objects }, token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ControlSetting>> SettingsAsync(string objectId,
        IReadOnlyList<ControlSetting> settings, CancellationToken token)
    {
        if (!settings.Any(setting => setting.CapabilityId == GpuVfCurveScalarAdapter.LegacyCapabilityId)) return settings;
        if (settings.Any(setting => setting.CapabilityId == GpuVfCurveCapabilities.Core))
            throw new GpuVfCurveRequestException(new("gpu-vf-curve-legacy-conflict"));
        var result = new List<ControlSetting>(settings.Count);
        foreach (var setting in settings)
        {
            if (setting.CapabilityId != GpuVfCurveScalarAdapter.LegacyCapabilityId) { result.Add(setting); continue; }
            if (setting.Number is not { } number || !double.IsFinite(number)
                || setting.Toggle is not null || setting.Curve is not null)
                throw new GpuVfCurveRequestException(new("gpu-vf-curve-legacy-offset-invalid"));
            var mhz = ControlUnitAdapter.Convert(number, setting.Unit, ControlUnits.Megahertz)
                ?? throw new GpuVfCurveRequestException(new("control-unit-mismatch"));
            try
            {
                var id = await curves.EnsureUniformAsync(objectId,
                    mhz * GpuVfCurveScalarAdapter.KilohertzPerMegahertz, token).ConfigureAwait(false);
                result.Add(new(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: id));
            }
            catch (GpuVfCurveRequestException error)
            {
                // Offline/unknown ABI settings remain intact for a later explicit migration; never invent a voltage table.
                logger?.LogWarning("GPU V/F offset migration pending for {ObjectId}: {Reason}", objectId, error.Reason.Code);
                return settings;
            }
        }
        return result;
    }
}
