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
    private readonly ILogger<AmdCpuControlWriter>? logger;
    internal AmdCpuControlResource Resource => resource;
    public AmdCpuControlWriter(HardwareBridgeClient bridge, IHostEnvironment environment, ILogger<AmdCpuControlWriter>? logger = null,
        StartupCapabilitySet? capabilities = null)
    {
        this.logger = logger;
        resource = new(bridge, new(file => ControlDataPaths.FilePath(environment.ContentRootPath, file, capabilities)), Describe,
            logger is null ? null : (error, code, args) => logger.LogWarning(error, "{ProviderCode} {Arguments}", Describe(code, args), args));
    }
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
        return new(value.CanWrite,Reason(value.Reason),Range(value.Range),value.UnavailableKind switch
        { AmdCpuControlUnavailableKind.Component => ControlUnavailableKinds.Component,
          AmdCpuControlUnavailableKind.NotImplemented => ControlUnavailableKinds.NotImplemented,
          AmdCpuControlUnavailableKind.Platform => ControlUnavailableKinds.Platform, _ => null },value.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability cap, ControlSetting setting, CancellationToken token)
    {
        if (AmdCpuControlFormatter.Field(cap.Id) is not { } field) return new(target.Id,cap.Id,ControlApplyStatuses.Failed,Describe(AmdCpuControlMessage.NotSupported,[]));
        var value = await resource.WriteAsync(Target(target),new(field,Range(cap.Range)),new(field,setting.Number,setting.Unit),token);
        return new(target.Id,cap.Id,value.Status == AmdCpuControlStatus.Applied ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,Reason(value.Message))
        {
            Evidence = value.Status == AmdCpuControlStatus.Applied ? new(field == AmdCpuControlField.PowerBoostScalar
                ? FieldWriteEvidence.AcceptedOnly : FieldWriteEvidence.SettingReadbackTolerance) : null
        };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability cap, CancellationToken token)
    {
        if (AmdCpuControlFormatter.Field(cap.Id) is not { } field) return null;
        var value = await resource.ReadAsync(Target(target),new(field,Range(cap.Range)),token);
        return value is null ? null : new(target.Id,cap.Id,Number:value.Number,Unit:value.Unit,UnreadableReason:Reason(value.UnreadableReason));
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability cap, IReadOnlySet<string> configured,CancellationToken token,ControlSetting? previousSetting = null)
        => AmdCpuControlFormatter.Field(cap.Id) is { } field ? resource.RestoreAsync(Target(target),new(field,Range(cap.Range)),token) : Task.FromResult(false);
    internal static bool IsUsableReading(JsonElement value,string? field) => AmdCpuControlResource.IsUsableReading(value,field);
    public void Dispose() => resource.Dispose();
    private ControlApplyReason? Reason(string? value)
    {
        if (value is null) return null;
        const string prefix = "amd-cpu.";
        if (value is "control-helper-runtime-missing" or "control-helper-startup-failed"
            || value.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse<AmdCpuControlMessage>(value[prefix.Length..], out var code) && Enum.IsDefined(code))
            return new(value);
        logger?.LogDebug("AMD CPU control channel unavailable: {Detail}", value);
        return new("control-backend-unavailable");
    }
    private static string Describe(AmdCpuControlMessage code, IReadOnlyList<object?> arguments)
    {
        return "amd-cpu." + code.ToString();
    }
}
