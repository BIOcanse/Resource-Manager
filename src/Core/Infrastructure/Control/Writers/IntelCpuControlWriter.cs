using ResourceManager.ReadWrite;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product path/target/capability/copy adapter over the complete original SDK execution owner.</summary>
public sealed class IntelCpuControlWriter : IControlWriter, IControlDetectionCache, IDisposable
{
    internal IntelCpuControlResource Resource { get; }
    public IntelCpuControlWriter(IHostEnvironment environment)
        : this(() => IntelCpuHardwareBinding.Open(environment.ContentRootPath), Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "intel-cpu-baselines.json")) { }
    internal IntelCpuControlWriter(Func<IIntelCpuHardware> open, string baselinePath)
        => Resource = new(open, baselinePath, IntelCpuControlCopy.Describe);

    public string? ChannelOf(ControlObject target, string capabilityId)
        => Resource.ChannelOf(Target(target), capabilityId) is null ? null : ControlChannels.IntelMsr;
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        var result = Resource.Probe(Target(target), Capability(capability));
        return new(result.CanWrite, result.Reason, Range(result.Range), result.UnavailableKind, result.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
        ControlSetting setting, CancellationToken cancellationToken)
    {
        var result = await Resource.WriteAsync(Target(target), Capability(capability),
            new(setting.CapabilityId, setting.Number, setting.Unit), cancellationToken).ConfigureAwait(false);
        return new(result.ObjectId, result.CapabilityId, result.Status, result.Message)
        {
            Evidence = result.Status == ControlApplyStatuses.Applied
                ? new(IntelCpuControlResource.VoltagePlane(capability.Id) is not null
                    ? FieldWriteEvidence.EncodedMailboxReadback : FieldWriteEvidence.MaskedRegisterReadback) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken cancellationToken)
    {
        var result = await Resource.ReadAsync(Target(target), Capability(capability), cancellationToken).ConfigureAwait(false);
        return result is null ? null : new(result.ObjectId, result.CapabilityId, Number: result.Number, Unit: result.Unit);
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken, ControlSetting? previousSetting = null)
        => Resource.RestoreAsync(Target(target), Capability(capability), stillConfigured, cancellationToken,
            previousSetting is null ? null : new(previousSetting.CapabilityId, previousSetting.Number, previousSetting.Unit));
    public void ResetDetection() => Resource.ResetDetection();
    public void Dispose() => Resource.Dispose();
    private static IntelCpuControlTarget Target(ControlObject target) => new(target.Id,
        target.Kind == ControlObjectKinds.Cpu && target.Platform.Vendor == ControlVendors.Intel
            && target.Platform.OperatingSystem == ControlOperatingSystems.Windows);
    private static IntelCpuControlCapability Capability(ControlCapability capability) => new(capability.Id,
        capability.Range is { } range ? new(range.Minimum, range.Maximum, range.Step, range.Unit, range.DefaultValue) : null);
    private static ControlNumberRange? Range(IntelCpuControlNumberRange? range)
        => range is null ? null : new(range.Minimum, range.Maximum, range.Step, range.Unit, range.DefaultValue);
}

internal static class IntelCpuControlCopy
{
    internal static string Describe(IntelCpuControlMessage message, IReadOnlyList<object?> arguments)
    {
        var format = message switch
        {
            IntelCpuControlMessage.MmioWriteNotImplemented => "IntelMCHBAR 官方签名模块只开放读取；MMIO 写入尚未具备可部署的签名模块。",
            IntelCpuControlMessage.RegisterWriteNotImplemented => "IntelMSR 官方签名模块未开放这一寄存器；该项写入尚未落实。",
            IntelCpuControlMessage.MailboxModuleUnavailable => "当前签名模块未开放 OC 邮箱。",
            IntelCpuControlMessage.FirmwarePowerLimitLocked => "固件锁定了 Intel 功耗上限寄存器。",
            IntelCpuControlMessage.PowerLimitModuleUnavailable => "当前模块未开放功耗上限写入。",
            IntelCpuControlMessage.NumberRequired => "需要有效数值。",
            IntelCpuControlMessage.OutOfRange => "数值超出当前硬件范围。",
            IntelCpuControlMessage.VoltageVerified => "已写入并回读 {0:G6} mV。",
            IntelCpuControlMessage.VoltageReadbackMismatch => "电压回读不一致；固件可能锁定或联动了电压域。",
            IntelCpuControlMessage.FirmwarePowerRegisterLocked => "固件已锁定功耗寄存器。",
            IntelCpuControlMessage.PowerReadbackMismatch => "写入已返回，但回读不一致；固件可能拒绝或覆盖了设置。",
            IntelCpuControlMessage.PowerVerified => "已写入并回读；实际硬件档位 {0:G6} {1}。",
            IntelCpuControlMessage.BaselineUnavailable => "无法取得或保存调整前的值，不能保证撤销恢复。",
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
        return arguments.Count == 0 ? format
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, arguments.ToArray());
    }
}

