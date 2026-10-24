using ResourceManager.ReadWrite;
using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.App.Hosting.StartupCapabilities;
namespace ResourceManager.App.Infrastructure.Control.Writers;
/// <summary>Product routing/copy/baseline-path adapter; execution and the original baseline map are SDK owned.</summary>
public sealed class AmdCpuControlWriter : IControlWriter, IDisposable
{
    private readonly AmdCpuControlResource resource;
    internal AmdCpuControlResource Resource => resource;
    public AmdCpuControlWriter(HardwareBridgeClient bridge, IHostEnvironment environment, ILogger<AmdCpuControlWriter>? logger = null,
        StartupCapabilitySet? capabilities = null)
        => resource = new(bridge, new(file => ControlDataPaths.FilePath(environment.ContentRootPath, file, capabilities)), Describe,
            logger is null ? null : (error, code, args) => logger.LogWarning(error, "{Detail}", Describe(code, args)));
    internal const string PowerLimitCapabilityId = "cpu.power-limit";
    internal const string SlowPowerLimitCapabilityId = "cpu.slow-power-limit";
    internal const string FastPowerLimitCapabilityId = "cpu.fast-power-limit";
    internal const string TdcLimitCapabilityId = "cpu.tdc-limit";
    internal const string EdcLimitCapabilityId = "cpu.edc-limit";
    internal const string TemperatureLimitCapabilityId = "cpu.temperature-limit";
    internal const string PowerBoostScalarCapabilityId = "cpu.pbo-scalar";
    internal const string CurveOptimizerCapabilityId = "cpu.curve-optimizer";
    private static AmdCpuControlTarget Target(ControlObject target) => new(target.Id, target.Kind == ControlObjectKinds.Cpu && target.Platform.Vendor == ControlVendors.Amd);
    private static AmdCpuControlRange? Range(ControlNumberRange? r) => r is null ? null : new(r.Minimum,r.Maximum,r.Step,r.Unit,r.DefaultValue);
    private static ControlNumberRange? Range(AmdCpuControlRange? r) => r is null ? null : new(r.Minimum,r.Maximum,r.Step,r.Unit,r.DefaultValue);
    public string? ChannelOf(ControlObject target, string id) => Target(target).IsAmdCpu && AmdCpuControlFormatter.Field(id) is not null ? ControlChannels.AmdSmu : null;
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability cap)
    {
        if (AmdCpuControlFormatter.Field(cap.Id) is not { } field) return ControlWriteAvailability.NotMine;
        var value = resource.Probe(Target(target), new(field, Range(cap.Range)));
        return new(value.CanWrite,value.Reason,Range(value.Range),value.UnavailableKind switch
        { AmdCpuControlUnavailableKind.Component => ControlUnavailableKinds.Component,
          AmdCpuControlUnavailableKind.NotImplemented => ControlUnavailableKinds.NotImplemented,
          AmdCpuControlUnavailableKind.Platform => ControlUnavailableKinds.Platform, _ => null },value.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability cap, ControlSetting setting, CancellationToken token)
    {
        if (AmdCpuControlFormatter.Field(cap.Id) is not { } field) return new(target.Id,cap.Id,ControlApplyStatuses.Failed,Describe(AmdCpuControlMessage.NotSupported,[]));
        var value = await resource.WriteAsync(Target(target),new(field,Range(cap.Range)),new(field,setting.Number,setting.Unit),token);
        return new(target.Id,cap.Id,value.Status == AmdCpuControlStatus.Applied ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,value.Message)
        {
            Evidence = value.Status == AmdCpuControlStatus.Applied ? new(field == AmdCpuControlField.PowerBoostScalar
                ? FieldWriteEvidence.AcceptedOnly : FieldWriteEvidence.SettingReadbackTolerance) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability cap, CancellationToken token)
    {
        if (AmdCpuControlFormatter.Field(cap.Id) is not { } field) return null;
        var value = await resource.ReadAsync(Target(target),new(field,Range(cap.Range)),token);
        return value is null ? null : new(target.Id,cap.Id,Number:value.Number,Unit:value.Unit,UnreadableReason:value.UnreadableReason);
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability cap, IReadOnlySet<string> configured,CancellationToken token,ControlSetting? previousSetting = null)
        => AmdCpuControlFormatter.Field(cap.Id) is { } field ? resource.RestoreAsync(Target(target),new(field,Range(cap.Range)),token) : Task.FromResult(false);
    internal static bool IsUsableReading(JsonElement value,string? field) => AmdCpuControlResource.IsUsableReading(value,field);
    public void Dispose() => resource.Dispose();
    private static string Describe(AmdCpuControlMessage code, IReadOnlyList<object?> arguments)
    {
        var format = code switch
        {
            AmdCpuControlMessage.BridgeMissing => "需要硬件写入辅助进程。",
            AmdCpuControlMessage.BridgeSilent => "辅助进程没应答。",
            AmdCpuControlMessage.NotSupported => "这颗处理器没有这一项。",
            AmdCpuControlMessage.NotEffective => "写下去了，回读没变。",
            AmdCpuControlMessage.TemperatureLimitUnverifiable => "还认不出这颗处理器温度墙的当前值，没有回读就不写。",
            AmdCpuControlMessage.CurrentLimitMappingUnconfirmed => "命令和电流墙的对应关系还没对准，写错会把处理器掐住，先不开放。",
            AmdCpuControlMessage.NumberRequired => "这一项要一个数值，收到的不是。",
            AmdCpuControlMessage.SmuRejected => "SMU 拒绝了这次写入。",
            AmdCpuControlMessage.Unreadable => "这一项没有可读的当前值。",
            AmdCpuControlMessage.ReadingMissing => "这颗处理器没有报出这一项的当前值。",
            AmdCpuControlMessage.StatusRejected => "SMU 没收下这次写入（{0}）。",
            AmdCpuControlMessage.StatusWarning => "处理器 {0} 写入被 SMU 拒绝或超时：{1}。",
            AmdCpuControlMessage.ReadbackWarning => "处理器 {0}（{1}）没写进去：要 {2}，回读 {3}。",
            AmdCpuControlMessage.ReadBaselineWarning => "读不到基线：{0}",
            AmdCpuControlMessage.WriteBaselineWarning => "存不下基线：{0}",
            AmdCpuControlMessage.BaselineUnavailable => "无法确认并持久保存此处理器的恢复基线，未执行写入。",
            _ => code.ToString()
        };
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,format,arguments.ToArray());
    }
}


