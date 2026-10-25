using ResourceManager.ReadWrite;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.App.Hosting.StartupCapabilities;
namespace ResourceManager.App.Infrastructure.Control.Writers;
/// <summary>Product route/consent state/copy/path adapter; both original execution paths are in one SDK resource.</summary>
public sealed class IntegratedGpuControlWriter : IControlWriter, IDisposable
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string IntelTargetMissing = "integrated-gpu.IntelTargetMissing";
    private readonly IntegratedGpuControlResource resource;
    private readonly ILogger<IntegratedGpuControlWriter>? logger;
    internal IntegratedGpuControlResource Resource => resource;
    public IntegratedGpuControlWriter(HardwareBridgeClient bridge,IControlOverclockConsent consent,IHostEnvironment environment,ILogger<IntegratedGpuControlWriter>? logger=null,
        StartupCapabilitySet? capabilities = null)
        : this(bridge,consent,environment,new IntelGraphicsControlBridge(),logger,capabilities) { }
    internal IntegratedGpuControlWriter(HardwareBridgeClient bridge,IControlOverclockConsent consent,IHostEnvironment environment,
        IIntelGraphicsControlHardware intel,ILogger<IntegratedGpuControlWriter>? logger=null,StartupCapabilitySet? capabilities=null)
    {
        this.logger = logger;
        resource=new(bridge,intel,new(ControlDataPaths.FilePath(environment.ContentRootPath,"amd-igpu-curve-optimizer-baseline.txt",capabilities),consent.IsAcceptedAsync),Describe,
            logger is null ? null : (error,code,args) => logger.LogWarning(error,"{ProviderCode} {Arguments}",Describe(code,args),args));
    }
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
        return new(v.CanWrite,Reason(v.Reason),Range(v.Range),v.UnavailableKind switch
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
        return new(t.Id,cap.Id,v.Status==IntegratedGpuControlStatus.Applied ? ControlApplyStatuses.Applied:ControlApplyStatuses.Failed,Reason(v.Message))
        {
            Evidence = v.Status == IntegratedGpuControlStatus.Applied ? new(v.ReadbackVerified
                ? FieldWriteEvidence.SettingReadbackTolerance : FieldWriteEvidence.AcceptedOnly) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject t,ControlCapability cap,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if (IntegratedGpuControlFormatter.Field(cap.Id) is not { } f) return null;
        var v=await resource.ReadAsync(Target(t),new(f,Range(cap.Range)),token);
        return v is null ? null:new(t.Id,cap.Id,Number:v.Number,Unit:v.Unit,UnreadableReason:Reason(v.UnreadableReason));
    }
    public Task<bool> RestoreAsync(ControlObject t,ControlCapability cap,IReadOnlySet<string> configured,CancellationToken token,ControlSetting? previousSetting=null)
        => IntegratedGpuControlFormatter.Field(cap.Id) is { } f ? resource.RestoreAsync(Target(t),new(f,Range(cap.Range)),token):Task.FromResult(false);
    public void Dispose()=>resource.Dispose();
    private ControlApplyReason? Reason(string? value)
    {
        if (value is null) return null;
        const string prefix = "integrated-gpu.";
        if (value is "control-helper-runtime-missing" or "control-helper-startup-failed"
            || value.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse<IntegratedGpuControlMessage>(value[prefix.Length..], out var code) && Enum.IsDefined(code))
            return new(value);
        logger?.LogDebug("Integrated GPU control channel unavailable: {Detail}", value);
        return new("control-backend-unavailable");
    }
    private static string Describe(IntegratedGpuControlMessage code,IReadOnlyList<object?> arguments)
    {
        return "integrated-gpu." + code.ToString();
    }
}
