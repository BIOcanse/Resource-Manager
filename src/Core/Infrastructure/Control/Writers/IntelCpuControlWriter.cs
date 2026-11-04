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
    private readonly ILogger<IntelCpuControlWriter>? logger;
    public IntelCpuControlWriter(IHostEnvironment environment, ILogger<IntelCpuControlWriter>? logger = null)
        : this(() => IntelCpuHardwareBinding.Open(environment.ContentRootPath), Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "intel-cpu-baselines.json"), logger) { }
    internal IntelCpuControlWriter(Func<IIntelCpuHardware> open, string baselinePath, ILogger<IntelCpuControlWriter>? logger = null)
    {
        this.logger = logger;
        Resource = new(open, baselinePath, IntelCpuControlCopy.Describe);
    }

    public string? ChannelOf(ControlObject target, string capabilityId)
        => Resource.ChannelOf(Target(target), capabilityId) is null ? null : ControlChannels.IntelMsr;
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        var result = Resource.Probe(Target(target), Capability(capability));
        return new(result.CanWrite, Reason(result.Reason, result.UnavailableKind == ControlUnavailableKinds.Component
            ? "control-helper-missing" : "control-backend-unavailable"), Range(result.Range), result.UnavailableKind, result.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
        ControlSetting setting, CancellationToken cancellationToken)
    {
        var result = await Resource.WriteAsync(Target(target), Capability(capability),
            new(setting.CapabilityId, setting.Number, setting.Unit), cancellationToken).ConfigureAwait(false);
        var reason = Reason(result.Message, "control-write-failed");
        return new(result.ObjectId, result.CapabilityId, result.Status, reason is null ? null : reason with
        { Arguments = new(Unit: result.Unit, Requested: result.Requested, Observed: result.Observed) })
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

    private ControlApplyReason? Reason(string? value, string failure)
    {
        if (value is null) return null;
        const string prefix = "intel-cpu.";
        if (value.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse<IntelCpuControlMessage>(value[prefix.Length..], out var code) && Enum.IsDefined(code))
            return new(value);
        logger?.LogWarning("Intel control failure: {Detail}", value);
        return new(failure);
    }
}

internal static class IntelCpuControlCopy
{
    internal static string Describe(IntelCpuControlMessage message, IReadOnlyList<object?> arguments)
    {
        return "intel-cpu." + message.ToString();
    }
}
