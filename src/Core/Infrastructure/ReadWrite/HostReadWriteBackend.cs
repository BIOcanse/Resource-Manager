using System.Collections.Immutable;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Fields;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Product catalog publication and control-write policy over the single SDK binding implementation.</summary>
public sealed class HostReadWriteBackend : IReadWriteBackend
{
    private readonly MetricReadWriteBackend backend;
    private readonly IControlObjectCatalog controlCatalog;
    private readonly IControlWriteLayer writeLayer;
    private ControlObjectCatalog? discoveredControls;
    private readonly WindowsHardwareMetricSampler? controlSampler;

    public HostReadWriteBackend(IMetricSampler sampler, IMetricSnapshotObservationSource observations,
        HardwareMetricCatalogState catalogState, IControlObjectCatalog controlCatalog, IControlWriteLayer writeLayer)
    {
        controlSampler = sampler as WindowsHardwareMetricSampler;
        this.controlCatalog = controlCatalog;
        this.writeLayer = writeLayer;
        backend = new(() => catalogState.Catalog, request => observations.ReadLatest(request),
            (request, token) => new(sampler.CaptureSnapshotAsync(request, token)),
            snapshot =>
            {
                catalogState.PublishCatalogProbe(snapshot);
                // Cached by design: this callback projects the capture already
                // owned by the original sampler; another fetch would reenter it.
                discoveredControls = controlCatalog.ReadObjects();
            },
            (snapshot, binding) =>
            {
                if (WindowsMetricControlBindings.TryFind(binding) is not null)
                    return controlSampler?.ReadControlSupport(binding)
                        ?? OperationSupport.Unsupported("host-control-owner-not-connected");
                var supported = binding.Field.Reading?.SourceAvailability != "unsupported";
                return new(supported, supported ? null : "field-read-unsupported",
                    MetricReadWriteBackend.DependenciesOf(binding.SourceId));
            },
            binding =>
            {
                if (WindowsMetricControlBindings.TryFind(binding) is { } control)
                {
                    var support = controlSampler?.QueryControl(binding)
                        ?? OperationSupport.Unsupported("host-control-owner-not-connected");
                    var controlTarget = FindManagedTarget(binding, control, discoveredControls);
                    var capability = controlTarget?.Capabilities.SingleOrDefault(item => item.Id == control.RawCapability);
                    return capability is { Supported: true } ? support
                        : new(false, capability?.UnavailableReason?.Code ?? "host-control-target-not-bound", support.Dependencies);
                }
                var target = FindPowerLimitTarget(binding, discoveredControls);
                var write = target?.Capabilities.SingleOrDefault(static item => item.Id == "gpu.power-limit");
                return write is null ? OperationSupport.Unsupported("field-write-not-bound")
                    : new(write.Supported, write.Supported ? null : write.UnavailableReason?.Code ?? "field-write-unavailable",
                        [NativeMetricSnapshotSourceCatalog.NvidiaNvml]);
            }, WriteFieldAsync, binding => controlSampler?.ControlContract(binding), ids => observations.ReadItems(ids));
    }
    public ReadWriteCatalog Catalog => backend.Catalog;
    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken token)
    {
        await backend.DiscoverAsync(token);
        return Catalog;
    }
    public FieldReading ReadCached(FieldReference field) => backend.ReadCached(field);
    public ImmutableArray<FieldReading> ReadCachedBatch(ImmutableArray<FieldReference> fields) => backend.ReadCachedBatch(fields);
    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken token)
        => backend.CollectAsync(fields, token);
    internal MetricFieldSelection[] ResolveBindings(ImmutableArray<FieldReference> fields) => backend.Select(fields);
    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken token)
        => backend.WriteAsync(field, value, token);

    private async ValueTask<FieldWriteResult> WriteFieldAsync(FieldReference field, MetricDeviceFieldBinding binding,
        FieldValue value, CancellationToken token)
    {
        if (WindowsMetricControlBindings.TryFind(binding) is { } contract)
        {
            if (!contract.Implemented || controlSampler is null)
                return new(field, ControlApplyStatuses.Unsupported, "host-control-owner-not-connected");
            // Keep fresh physical validation and this original permission-layer
            // callback inside the original sampler's same SampleGate.
            return await controlSampler.ExecuteBoundControlWriteAsync(field, binding, value, async cancellationToken =>
            {
                // Fresh physical capture is already held under SampleGate.
                // Reuse its catalog for both identity and permission checks.
                var controls = controlCatalog.ReadObjects();
                var controlTarget = FindManagedTarget(binding, contract, controls);
                if (controlTarget is null)
                    return new(field, ControlApplyStatuses.Unsupported, "field-write-device-unavailable");
                double? number = value.Number;
                if (binding.Field.ValueKind == "signed64")
                {
                    if (value.Integer is not { FitsSigned32: true } integer)
                        return new(field, ControlApplyStatuses.Failed, "field-write-invalid-integer");
                    number = integer.ToSigned64();
                }
                var report = await writeLayer.WriteWithCatalogAsync(new([new(controlTarget.Id,
                    [new(contract.RawCapability, Number: number, Toggle: value.Boolean, Unit: contract.RawUnit)])]), controls, cancellationToken);
                var outcome = report.Outcomes.Single();
                return new(field, outcome.Status, outcome.Reason?.Code) { Evidence = outcome.Evidence };
            }, token);
        }
        // This binding uses the original discovered/captured control projection.
        // Keep it explicit rather than introducing a nested sampling demand.
        var powerControls = controlCatalog.ReadObjects();
        var target = FindPowerLimitTarget(binding, powerControls);
        if (target is null) return new(field, ControlApplyStatuses.Unsupported, "field-write-device-unavailable");
        // The existing layer owns instance checks, permission, driver selection,
        // range/unit validation and confirmed write results. No desired state is saved.
        var report = await writeLayer.WriteWithCatalogAsync(new([new(target.Id,
            [new("gpu.power-limit", Number: value.Number, Unit: binding.Field.Unit)])]), powerControls, token);
        var result = report.Outcomes.Single();
        return new(field, result.Status, result.Reason?.Code);
    }
    private static ControlObject? FindManagedTarget(MetricDeviceFieldBinding binding,
        WindowsMetricControlBinding contract, ControlObjectCatalog? controls)
    {
        if (controls is null || binding.ControlTarget?.RawObjectId is not { } rawObjectId) return null;
        var id = ResolveOriginalControlObjectId(controls, contract.Family, binding.Field.Resource, binding.AdapterKey);
        if (id != rawObjectId) return null;
        if (contract.Family == WindowsMetricControlFamily.AmdDesktopGpu
            && controls.Objects.SingleOrDefault(item => item.Id == id)?.ExpectedPhysicalPnp
                != binding.ControlTarget.GpuPnp) return null;
        return controls.Objects.SingleOrDefault(item => item.Id == id
            && item.Capabilities.Any(capability => capability.Id == contract.RawCapability));
    }

    // Called by the explicit Discover mapper over the original product identity
    // projection. Names and display ordinals never become execution identities.
    internal static string? ResolveOriginalControlObjectId(ControlObjectCatalog controls,
        WindowsMetricControlFamily family, MetricResource resource, ulong? adapterKey)
    {
        var cpu = family is WindowsMetricControlFamily.IntelCpu or WindowsMetricControlFamily.AmdCpu;
        var vendor = family is WindowsMetricControlFamily.IntelCpu or WindowsMetricControlFamily.IntelIntegratedGpu
            ? ControlVendors.Intel : ControlVendors.Amd;
        var fan = resource == MetricResource.Fan;
        if (cpu && resource != MetricResource.Cpu || !cpu && resource is not (MetricResource.Gpu or MetricResource.Fan)
            || fan && family != WindowsMetricControlFamily.AmdDesktopGpu) return null;
        var matches = controls.Objects.Where(item => item.Platform.OperatingSystem == ControlOperatingSystems.Windows
            && item.Platform.Vendor == vendor
            && item.Kind == (cpu ? ControlObjectKinds.Cpu : fan ? ControlObjectKinds.Fan : ControlObjectKinds.Gpu)
            && (cpu || adapterKey is { } key && key != 0 && item.AdapterKey == key)
            && (cpu || item.GpuAttachment == (family == WindowsMetricControlFamily.AmdDesktopGpu
                ? ControlGpuAttachments.Discrete : ControlGpuAttachments.Integrated))).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }
    private static ControlObject? FindPowerLimitTarget(MetricDeviceFieldBinding binding, ControlObjectCatalog? controls)
    {
        if (binding.Field.Code != MetricFieldCode.GpuPowerLimit || binding.Field.Vendor != MetricVendor.Nvidia
            || binding.SourceId != NativeMetricSnapshotSourceCatalog.NvidiaNvml || binding.AdapterKey is not { } key || key == 0)
            return null;
        var matches = controls?.Objects.Where(item => item.Kind == ControlObjectKinds.Gpu
            && item.Platform.Vendor == ControlVendors.Nvidia && item.AdapterKey == key).ToArray() ?? [];
        return matches.Length == 1 ? matches[0] : null;
    }

}
