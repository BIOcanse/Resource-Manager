using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers.Amd;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

public sealed class AmdDesktopGpuControlWriter : IControlWriter
{
    private readonly IAdlxTuningHardware hardware;
    private readonly string baselinePath;
    private readonly object gate = new();
    private Dictionary<string, int>? baselines;
    public AmdDesktopGpuControlWriter(IHostEnvironment environment) : this(new AdlxTuningHardware(),
        Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "amd-gpu-baselines.json")) { }
    internal AmdDesktopGpuControlWriter(IAdlxTuningHardware hardware, string baselinePath)
    { this.hardware = hardware; this.baselinePath = baselinePath; }
    internal static int? Field(string id) => id switch
    {
        "gpu.power-limit-offset" => 0, "gpu.core-clock-minimum" => 1, "gpu.core-clock-maximum" => 2,
        "gpu.core-voltage" => 3, "gpu.memory-clock-maximum" => 4,
        "fan.minimum-rpm" => 5, "fan.target-rpm" => 6, "fan.zero-rpm" => 7,
        "gpu.core-clock-offset" => 8, "gpu.core-voltage-offset" => 9, _ => null
    };
    private static bool IsMine(ControlObject target, string id) => (target.Kind == ControlObjectKinds.Gpu && id.StartsWith("gpu.", StringComparison.Ordinal)
            || target.Kind == ControlObjectKinds.Fan && id.StartsWith("fan.", StringComparison.Ordinal))
        && target.Platform.OperatingSystem == ControlOperatingSystems.Windows && target.Platform.Vendor == ControlVendors.Amd
        && target.GpuAttachment == ControlGpuAttachments.Discrete && target.Terms?.Contains(ControlChassisKinds.Fixed) == true && Field(id) is not null;
    public string? ChannelOf(ControlObject target, string id) => IsMine(target, id) ? ControlChannels.Adlx : null;
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        if (!IsMine(target, capability.Id)) return ControlWriteAvailability.NotMine;
        lock (gate)
        {
            try
            {
                var value = hardware.Read(target, Field(capability.Id)!.Value);
                return ControlWriteAvailability.Yes(new(value.Minimum, value.Maximum, value.Step, Unit(capability.Id)));
            }
            catch (Exception error) when (Unavailable(error)) { return ControlWriteAvailability.No(error.Message,
                kind: error is AdlxUnknownGenerationException ? ControlUnavailableKinds.NotImplemented
                    : error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ? ControlUnavailableKinds.Component : ControlUnavailableKinds.Platform); }
        }
    }
    public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Unsupported, "此路由仅支持固定式 AMD 独显。"));
        lock (gate)
        {
            try
            {
                var field = Field(capability.Id)!.Value;
                var before = hardware.Read(target, field);
                var requested = capability.Id == "fan.zero-rpm" ? setting.Toggle is { } enabled ? enabled ? 1d : 0d : (double?)null : setting.Number;
                if (requested is not { } number || !double.IsFinite(number) || number != Math.Truncate(number)
                    || number < before.Minimum || number > before.Maximum || (number - before.Minimum) % before.Step != 0)
                    return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, "数值不符合 ADLX 当前范围和步长。"));
                CaptureBaseline(before.Pnp, field, before.Current);
                hardware.Write(before.Pnp, field, (int)number);
                var after = hardware.Read(target, field);
                return Task.FromResult(Result(target, capability,
                    after.Pnp == before.Pnp && after.Current == (int)number ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,
                    after.Pnp == before.Pnp && after.Current == (int)number ? "已写入并回读。" : "显卡身份或调节值回读不一致。"));
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, error.Message)); }
        }
    }
    public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return Task.FromResult<ControlActualValue?>(null);
        lock (gate)
        {
            try
            {
                var value = hardware.Read(target, Field(capability.Id)!.Value);
                return Task.FromResult<ControlActualValue?>(capability.Id == "fan.zero-rpm"
                    ? new(target.Id, capability.Id, Toggle: value.Current != 0)
                    : new(target.Id, capability.Id, Number: value.Current, Unit: Unit(capability.Id)));
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult<ControlActualValue?>(null); }
        }
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> stillConfigured, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return Task.FromResult(false);
        lock (gate)
        {
            try
            {
                LoadBaselines();
                var field = Field(capability.Id)!.Value;
                var current = hardware.Read(target, field);
                if (!baselines!.TryGetValue(Key(current.Pnp, field), out var baseline)) return Task.FromResult(false);
                if (field == 1 && current.GfxMode == AdlxGfxMode.Unknown) return Task.FromResult(false);
                if (field is 1 or 2 && current.GfxMode == AdlxGfxMode.Absolute)
                {
                    var siblingField = field == 1 ? 2 : 1;
                    var sibling = hardware.Read(target, siblingField);
                    if (sibling.Pnp != current.Pnp) return Task.FromResult(false);
                    if (field == 1 ? baseline > sibling.Current : baseline < sibling.Current)
                    {
                        var siblingId = field == 1 ? "gpu.core-clock-maximum" : "gpu.core-clock-minimum";
                        if (stillConfigured.Contains(siblingId) || !baselines.TryGetValue(Key(current.Pnp, siblingField), out var siblingBaseline))
                            return Task.FromResult(false);
                        // Both limits are being released. Restore the blocking sibling first,
                        // retaining its baseline until its own release call verifies it.
                        hardware.Write(current.Pnp, siblingField, siblingBaseline);
                        var verifiedSibling = hardware.Read(target, siblingField);
                        if (verifiedSibling.Pnp != current.Pnp || verifiedSibling.Current != siblingBaseline) return Task.FromResult(false);
                    }
                }
                hardware.Write(current.Pnp, field, baseline);
                var after = hardware.Read(target, field);
                var restored = after.Pnp == current.Pnp && after.Current == baseline;
                if (restored)
                {
                    var next = new Dictionary<string, int>(baselines!);
                    next.Remove(Key(current.Pnp, field));
                    SaveBaselines(next);
                }
                return Task.FromResult(restored);
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult(false); }
        }
    }
    private static string Unit(string id) => id == "gpu.power-limit-offset" ? ControlUnits.Percent
        : id is "fan.minimum-rpm" or "fan.target-rpm" ? "RPM" : id == "fan.zero-rpm" ? ControlUnits.None
        : id is "gpu.core-voltage" or "gpu.core-voltage-offset" ? ControlUnits.Millivolt : ControlUnits.Megahertz;
    private static string Key(string pnp, int field) => $"{pnp.ToUpperInvariant()}\n{field}";
    private void LoadBaselines() => baselines ??= File.Exists(baselinePath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(baselinePath)) ?? [] : [];
    private void CaptureBaseline(string pnp, int field, int value)
    {
        LoadBaselines();
        var key = Key(pnp, field);
        if (baselines!.ContainsKey(key)) return;
        var next = new Dictionary<string, int>(baselines) { [key] = value };
        SaveBaselines(next);
    }
    private void SaveBaselines(Dictionary<string, int> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        File.WriteAllText(baselinePath + ".tmp", JsonSerializer.Serialize(next));
        File.Move(baselinePath + ".tmp", baselinePath, overwrite: true);
        baselines = next;
    }
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException;
    private static ControlApplyOutcome Result(ControlObject target, ControlCapability capability, string status, string? reason) => new(target.Id, capability.Id, status, reason);
}
