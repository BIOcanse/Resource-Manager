using ResourceManager.ReadWrite;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
namespace ResourceManager.App.Infrastructure.Control.Writers;
/// <summary>Product route/consent state/copy/path adapter; both original execution paths are in one SDK resource.</summary>
public sealed class IntegratedGpuControlWriter : IControlWriter, IDisposable
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string IntelTargetMissing = "无法将所选核显与 Intel 驱动设备唯一对应，未读取或写入其他显卡。";
    private readonly IntegratedGpuControlResource resource;
    internal IntegratedGpuControlResource Resource => resource;
    public IntegratedGpuControlWriter(HardwareBridgeClient bridge,IControlOverclockConsent consent,IHostEnvironment environment,ILogger<IntegratedGpuControlWriter>? logger=null)
        : this(bridge,consent,environment,new IntelGraphicsControlBridge(),logger) { }
    internal IntegratedGpuControlWriter(HardwareBridgeClient bridge,IControlOverclockConsent consent,IHostEnvironment environment,
        IIntelGraphicsControlHardware intel,ILogger<IntegratedGpuControlWriter>? logger=null)
        => resource=new(bridge,intel,new(ControlDataPaths.FilePath(environment.ContentRootPath,"amd-igpu-curve-optimizer-baseline.txt"),consent.IsAcceptedAsync),Describe,
            logger is null ? null : (error,code,args) => logger.LogWarning(error,"{Detail}",Describe(code,args)));
    private static IntegratedGpuControlTarget Target(ControlObject t) => new(t.Id,
        t.Kind != ControlObjectKinds.Gpu || t.GpuAttachment != ControlGpuAttachments.Integrated ? IntegratedGpuControlRoute.Rejected
        : t.Platform.Vendor == ControlVendors.Amd ? IntegratedGpuControlRoute.AmdIntegrated
        : t.Platform.Vendor == ControlVendors.Intel ? IntegratedGpuControlRoute.IntelIntegrated : IntegratedGpuControlRoute.Rejected,t.AdapterKey);
    private static IntegratedGpuControlRange? Range(ControlNumberRange? r) => r is null ? null : new(r.Minimum,r.Maximum,r.Step,r.Unit,r.DefaultValue);
    private static ControlNumberRange? Range(IntegratedGpuControlRange? r) => r is null ? null : new(r.Minimum,r.Maximum,r.Step,r.Unit,r.DefaultValue);
    public string? ChannelOf(ControlObject t,string id) => Target(t).Route switch
    {
        IntegratedGpuControlRoute.AmdIntegrated when id == CurveOptimizerCapabilityId => ControlChannels.AmdSmu,
        IntegratedGpuControlRoute.IntelIntegrated when id == CoreClockOffsetCapabilityId => ControlChannels.Igcl,_=>null
    };
    public ControlWriteAvailability Probe(ControlObject t,ControlCapability cap)
    {
        if (IntegratedGpuControlFormatter.Field(cap.Id) is not { } f) return ControlWriteAvailability.NotMine;
        var v=resource.Probe(Target(t),new(f,Range(cap.Range)));
        return new(v.CanWrite,v.Reason,Range(v.Range),v.UnavailableKind switch
        { IntegratedGpuControlUnavailableKind.Component=>ControlUnavailableKinds.Component,
          IntegratedGpuControlUnavailableKind.Prerequisite=>ControlUnavailableKinds.Prerequisite,
          IntegratedGpuControlUnavailableKind.Platform=>ControlUnavailableKinds.Platform,_=>null },v.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject t,ControlCapability cap,ControlSetting setting,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IntegratedGpuControlFormatter.Field(cap.Id) is not { } f || setting.CapabilityId != cap.Id)
            return new(t.Id,cap.Id,ControlApplyStatuses.Failed,Describe(IntegratedGpuControlMessage.NotSupported,[]));
        var v=await resource.WriteAsync(Target(t),new(f,Range(cap.Range)),new(f,setting.Number,setting.Unit),token);
        return new(t.Id,cap.Id,v.Status==IntegratedGpuControlStatus.Applied ? ControlApplyStatuses.Applied:ControlApplyStatuses.Failed,v.Message)
        {
            Evidence = v.Status == IntegratedGpuControlStatus.Applied ? new(v.ReadbackVerified
                ? FieldWriteEvidence.SettingReadbackTolerance : FieldWriteEvidence.AcceptedOnly) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject t,ControlCapability cap,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if (IntegratedGpuControlFormatter.Field(cap.Id) is not { } f) return null;
        var v=await resource.ReadAsync(Target(t),new(f,Range(cap.Range)),token);
        return v is null ? null:new(t.Id,cap.Id,Number:v.Number,Unit:v.Unit,UnreadableReason:v.UnreadableReason);
    }
    public Task<bool> RestoreAsync(ControlObject t,ControlCapability cap,IReadOnlySet<string> configured,CancellationToken token,ControlSetting? previousSetting=null)
        => IntegratedGpuControlFormatter.Field(cap.Id) is { } f ? resource.RestoreAsync(Target(t),new(f,Range(cap.Range)),token):Task.FromResult(false);
    public void Dispose()=>resource.Dispose();
    private static string Describe(IntegratedGpuControlMessage code,IReadOnlyList<object?> arguments)
    {
        var format=code switch
        {
            IntegratedGpuControlMessage.BridgeMissing => "需要先安装硬件写入辅助进程。",
            IntegratedGpuControlMessage.BridgeSilent => "硬件写入辅助进程没有应答。",
            IntegratedGpuControlMessage.NotSupported => "这颗处理器的核显上没有这一项。",
            IntegratedGpuControlMessage.NotEffective => "写下去了，但回读的值没有变。",
            IntegratedGpuControlMessage.IntelDriverMissing => "读不到 Intel 显卡驱动自带的控制库，装上/更新显卡驱动之后才能调。",
            IntegratedGpuControlMessage.IntelTargetMissing => "无法将所选核显与 Intel 驱动设备唯一对应，未读取或写入其他显卡。",
            IntegratedGpuControlMessage.OverclockNotAccepted => "需要先同意超频免责声明。",
            IntegratedGpuControlMessage.BaselineUnavailable => "无法取得或保存调整前的值，不能保证撤销恢复。",
            IntegratedGpuControlMessage.NumberRequired => "这一项要一个数值，收到的不是。",
            IntegratedGpuControlMessage.SmuRejected => "SMU 拒绝了这次写入。",
            IntegratedGpuControlMessage.ReadingMissing => "这颗处理器的核显没有报出这一项的当前值。",
            IntegratedGpuControlMessage.WaiverRejected => "这块核显不接受超频设置。",
            IntegratedGpuControlMessage.DriverRejected => "驱动拒绝了这次写入。",
            IntegratedGpuControlMessage.BaselineWarning => "无法保存 AMD 核显 Curve Optimizer 恢复基线。",
            IntegratedGpuControlMessage.SmuRejectedWarning => "SMU 拒绝了核显电压写入：{0} 档。",
            IntegratedGpuControlMessage.IntelRejectedWarning => "Intel 核显拒绝了频率偏移写入：{0} MHz，结果码 {1}。",
            _=>code.ToString()
        };
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,format,arguments.ToArray());
    }
}


