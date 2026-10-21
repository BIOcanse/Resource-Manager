using ResourceManager.ReadWrite;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product route, persistence location and copy adapter over the original SDK ADLX control owner.</summary>
public sealed class AmdDesktopGpuControlWriter : IControlWriter, IDisposable
{
    private readonly AmdDesktopGpuControlResource resource;
    internal AmdDesktopGpuControlResource Resource => resource;
    public AmdDesktopGpuControlWriter(IHostEnvironment environment) : this(new AdlxTuningHardware(),
        Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "amd-gpu-baselines.json")) { }
    internal AmdDesktopGpuControlWriter(IAdlxTuningHardware hardware, string baselinePath)
        => resource = new(hardware, new(baselinePath));
    internal static int? Field(string id) => AmdDesktopGpuControlFormatter.Field(id) is { } field ? (int)field : null;
    private static AmdDesktopGpuControlTarget Target(ControlObject target)
        => new(target.AdapterIndex,
            target.Platform.OperatingSystem == ControlOperatingSystems.Windows && target.Platform.Vendor == ControlVendors.Amd
                && target.GpuAttachment == ControlGpuAttachments.Discrete && target.Terms?.Contains(ControlChassisKinds.Fixed) == true
                && target.Kind is ControlObjectKinds.Gpu or ControlObjectKinds.Fan
                ? AmdDesktopGpuControlRoute.WindowsAmdFixedDiscrete : AmdDesktopGpuControlRoute.Rejected,
            target.Kind == ControlObjectKinds.Fan ? AmdDesktopGpuControlObjectKind.Fan : AmdDesktopGpuControlObjectKind.Gpu, ExpectedPnp: target.ExpectedPhysicalPnp);
    public string? ChannelOf(ControlObject target, string id)
        => AmdDesktopGpuControlFormatter.Field(id) is { } field && AmdDesktopGpuControlResource.IsAdmitted(Target(target), field) ? ControlChannels.Adlx : null;
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        if (AmdDesktopGpuControlFormatter.Field(capability.Id) is not { } field) return ControlWriteAvailability.NotMine;
        var value = resource.Probe(Target(target), field);
        if (!value.IsAdmitted) return ControlWriteAvailability.NotMine;
        return value.CanWrite ? ControlWriteAvailability.Yes(new(value.Range!.Minimum, value.Range.Maximum, value.Range.Step, value.Range.Unit))
            : ControlWriteAvailability.No(value.Error!, kind: value.FailureKind switch
            {
                AmdDesktopGpuControlFailureKind.Component => ControlUnavailableKinds.Component,
                AmdDesktopGpuControlFailureKind.NotImplemented => ControlUnavailableKinds.NotImplemented,
                _ => ControlUnavailableKinds.Platform
            });
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AmdDesktopGpuControlFormatter.Field(capability.Id) is not { } field)
            return new(target.Id, capability.Id, ControlApplyStatuses.Unsupported, "此路由仅支持固定式 AMD 独显。");
        var value = await resource.WriteAsync(Target(target), field, new(setting.Number, setting.Toggle), cancellationToken);
        return new(target.Id, capability.Id, value.Status switch
        {
            AmdDesktopGpuControlStatus.Applied => ControlApplyStatuses.Applied,
            AmdDesktopGpuControlStatus.Unsupported => ControlApplyStatuses.Unsupported,
            _ => ControlApplyStatuses.Failed
        }, value.Message switch
        {
            AmdDesktopGpuControlMessage.Applied => "已写入并回读。",
            AmdDesktopGpuControlMessage.RouteRejected => "此路由仅支持固定式 AMD 独显。",
            AmdDesktopGpuControlMessage.InvalidRangeOrStep => "数值不符合 ADLX 当前范围和步长。",
            AmdDesktopGpuControlMessage.ReadbackMismatch => "显卡身份或调节值回读不一致。",
            _ => value.Error
        })
        {
            Evidence = value.Status == AmdDesktopGpuControlStatus.Applied
                ? new(FieldWriteEvidence.ExactIntegerAndPnpReadback) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AmdDesktopGpuControlFormatter.Field(capability.Id) is not { } field) return null;
        var value = await resource.ReadAsync(Target(target), field, cancellationToken);
        return value is null ? null : new(target.Id, capability.Id, Number: value.Number, Toggle: value.Toggle, Unit: value.Unit);
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken, ControlSetting? previousSetting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AmdDesktopGpuControlFormatter.Field(capability.Id) is not { } field) return Task.FromResult(false);
        var configured = new HashSet<AmdDesktopGpuControlField>();
        foreach (var id in stillConfigured)
            if (AmdDesktopGpuControlFormatter.Field(id) is { } active) configured.Add(active);
        return resource.RestoreAsync(Target(target), field, configured, cancellationToken);
    }
    public void Dispose() => resource.Dispose();
}



