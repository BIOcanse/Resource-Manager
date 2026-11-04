using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

/// <summary>Canonical producer binding over borrowed original resources. The caller
/// holds its existing SampleGate for Discover/Collect/Write/Restore. Only explicit
/// discovery declaration metadata is retained; numeric current, baselines, helper
/// lifecycle and native context remain with their original owners.</summary>
public sealed class WindowsMetricControlFields(WindowsMetricControlResources resources,
    Func<WindowsGpuAdapterInventoryRead> readInventory)
{
    private bool HasResources => resources.IntelCpuControl is not null || resources.AmdCpuControl is not null
        || resources.AmdDesktopGpuControl is not null || resources.IntegratedGpuControl is not null;
    private ImmutableDictionary<string, Declaration> declarations = ImmutableDictionary<string, Declaration>.Empty;
    private sealed record Declaration(MetricDeviceFieldBinding Binding, WindowsMetricControlBinding Entry,
        bool CanRead, bool CanWrite, string? Reason, FieldWriteContract Contract);
    private sealed record CpuDomain(string Identity, string Vendor, MetricDeviceDescriptor Device);
    private sealed record Facts(CpuDomain? Cpu, WindowsGpuAdapterInventoryRead? Inventory, bool FixedChassis);

    /// <summary>Explicit capability operation. Original AMD CPU/iGPU Probe can
    /// start the supplied helper and persist its existing restoration baseline.
    /// Never called by Query/Catalog or cached reads.</summary>
    public HardwareMetricSnapshot Discover(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HasResources) return snapshot;
        var facts = CaptureFacts();
        var mapperGpuFacts = resources.ControlResolveObjectId is null ? null : CreateMapperGpuFacts(snapshot, facts.Inventory);
        var next = ImmutableDictionary.CreateBuilder<string, Declaration>();
        foreach (var entry in WindowsMetricControlBindings.All)
        {
            if (entry.Family is WindowsMetricControlFamily.IntelCpu or WindowsMetricControlFamily.AmdCpu)
            {
                var vendor = entry.Family == WindowsMetricControlFamily.IntelCpu ? "GenuineIntel" : "AuthenticAMD";
                if (facts.Cpu is { } cpu && cpu.Vendor == vendor)
                    Add(entry, cpu.Identity, null, new(cpu.Identity), cpu.Device);
                continue;
            }
            if (!Complete(facts.Inventory)) continue;
            var candidates = facts.Inventory!.Adapters.Where(adapter => AdmittedGpu(entry, adapter, facts)).ToArray();
            if (entry.Family == WindowsMetricControlFamily.AmdIntegratedGpu && candidates.Length != 1) continue;
            foreach (var adapter in candidates)
            {
                var exact = WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter);
                var identity = entry.Definition.Resource == MetricResource.Fan ? exact + "|fan-tuning-group" : exact;
                Add(entry, identity, adapter,
                    new(entry.Family == WindowsMetricControlFamily.AmdIntegratedGpu ? facts.Cpu?.Identity : null,
                        exact, adapter.PhysicalIdentities.Single().PnpInstanceId.ToUpperInvariant()),
                    GpuDevice(adapter, entry.Definition.Resource == MetricResource.Fan));
            }
        }
        Volatile.Write(ref declarations, next.ToImmutable());
        return AddBindings(snapshot);

        void Add(WindowsMetricControlBinding entry, string identity, WindowsGpuAdapter? adapter,
            WindowsMetricControlTarget target, MetricDeviceDescriptor device)
        {
            var key = adapter is null ? (ulong?)null : WindowsGpuAdapterIdentity.Pack(adapter.Luid);
            if (resources.ControlResolveObjectId is not null && adapter is not null && mapperGpuFacts is null) return;
            var rawId = resources.ControlResolveObjectId is { } mapper
                ? mapper(adapter is null ? snapshot : mapperGpuFacts!, entry.Family, entry.Definition.Resource, identity, key) : identity;
            if (string.IsNullOrWhiteSpace(rawId)) return;
            target = target with { RawObjectId = rawId };
            var binding = new MetricDeviceFieldBinding(identity, entry.Definition,
                "managed-control|" + Uri.EscapeDataString(identity) + "|" + entry.Definition.Name, entry.SourceId)
                { AdapterKey = key, ControlTarget = target, Device = device };
            next.Add(binding.RequestAddress, Probe(binding, entry, adapter));
        }
    }

    // The mapper needs current identity facts before this collection's native
    // values are admitted. This local input carries no available telemetry and
    // is never used as the published numeric frame.
    private static HardwareMetricSnapshot? CreateMapperGpuFacts(HardwareMetricSnapshot snapshot, WindowsGpuAdapterInventoryRead? inventory)
    {
        if (inventory is null || !Complete(inventory) || snapshot.Gpus.Count != inventory.Adapters.Count
            || snapshot.Gpus.Select(static gpu => gpu.Index).Distinct().Count() != snapshot.Gpus.Count
            || inventory.Adapters.Select(static adapter => adapter.Index).Distinct().Count() != inventory.Adapters.Count)
            return null;
        var adapters = inventory.Adapters.ToDictionary(static adapter => adapter.Index);
        foreach (var gpu in snapshot.Gpus)
        {
            if (!adapters.TryGetValue(gpu.Index, out var adapter)
                || string.IsNullOrEmpty(gpu.IdentityKey)
                || gpu.IdentityKey != WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter)) return null;
            if (gpu.AdapterKey is not { } key || key == 0
                || key != WindowsGpuAdapterIdentity.Pack(adapter.Luid)) return null;
        }
        return snapshot with { GpuInventory = new(SamplingObservationStatus.Current, inventory.Generation,
            inventory.ObservedAtUtcTicks, inventory.ObservedCount, inventory.SkippedCount, inventory.OverflowCount,
            inventory.TopologyFingerprint, inventory.Adapters.Select(adapter => new SchedulingGpuAdapterObservation(
                adapter.Index, WindowsGpuAdapterIdentity.Pack(adapter.Luid), SchedulingGpuCapabilityMask.None,
                SchedulingGpuMetricMask.None, SamplingObservationStatus.Unavailable, SamplingObservationStatus.Unavailable,
                0, 0, 0, inventory.Generation, inventory.ObservedAtUtcTicks)).ToArray()) };
    }

    public bool IsControlBinding(MetricDeviceFieldBinding binding) => WindowsMetricControlBindings.TryFind(binding) is not null;
    public OperationSupport Query(MetricDeviceFieldBinding binding)
    {
        var found = Find(binding);

        return found is null ? OperationSupport.Unsupported("control-field-not-discovered")
            : new(found.CanWrite, found.CanWrite ? null : found.Reason ?? "control-field-unavailable", found.Entry.WriteDependencies);
    }
    public OperationSupport ReadSupport(MetricDeviceFieldBinding binding)
    {
        var found = Find(binding);

        return found is null ? OperationSupport.Unsupported("control-field-not-discovered")
            : new(found.CanRead, found.CanRead ? null : found.Reason ?? "control-setting-read-unavailable", found.Entry.ReadDependencies);
    }
    public FieldWriteContract? GetContract(MetricDeviceFieldBinding binding) => Find(binding)?.Contract;

    /// <summary>The native projection already ignores these explicit addresses.
    /// This helper preserves request flags and never parses field names for routing.</summary>
    public MetricSampleRequest FilterNativeRequest(MetricSampleRequest request)
    {
        if (request.IsAll || request.IsCatalogProbe) return request;
        var managed = Volatile.Read(ref declarations);
        var ids = request.Ids.Where(id => !managed.ContainsKey(id));
        var native = request.IncludesAllGpuCoreMetrics ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(ids) : MetricSampleRequest.ForIds(ids);
        return request.IncludesCatalog ? native.WithCatalog() : native;
    }

    /// <summary>Read only explicitly selected configured settings, then return the
    /// passive projection for the caller's original atomic current publication.
    /// Does not Probe, save baselines or publish/update a second current.</summary>
    public async ValueTask<HardwareMetricSnapshot> ReadSelectedAsync(HardwareMetricSnapshot snapshot,
        MetricSampleRequest request, CancellationToken token = default, Action<string>? sourceQueryStarted = null)
    {
        if (!HasResources) return snapshot;
        var prepared = AddBindings(snapshot);
        var items = new Dictionary<string, MetricReadingValue>(prepared.Items, StringComparer.OrdinalIgnoreCase);
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(prepared.Datasets, StringComparer.OrdinalIgnoreCase);
        var selected = Volatile.Read(ref declarations).Values.Where(d => request.Includes(d.Binding.RequestAddress)).ToArray();
        if (selected.Length == 0) return prepared;
        var facts = CaptureFacts();
        foreach (var declaration in selected)
        {
            token.ThrowIfCancellationRequested();
            var binding = declaration.Binding;
            var entry = declaration.Entry;
            FieldValue? value = null;
            var status = SamplingObservationStatus.Unavailable;
            if (!entry.Implemented || !declaration.CanRead) status = !entry.Implemented || entry.ReadDependencies.IsEmpty
                ? SamplingObservationStatus.Unsupported : SamplingObservationStatus.Unavailable;
            else if (ResolveFresh(binding, entry, facts, out var adapter))
            {
                try
                {
                    sourceQueryStarted?.Invoke(binding.RequestAddress);
                    value = await ReadRawAsync(binding, entry, adapter, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { value = null; }
                status = value is not null ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable;
            }
            var at = DateTimeOffset.UtcNow;
            items.Remove(binding.RequestAddress);
            if (value is not null)
                items[binding.RequestAddress] = new(Id: binding.RequestAddress, NumericValue: value.Number, Unit: binding.Field.Unit, Percent: null)
                { IntegerValue = value.Integer, BooleanValue = value.Boolean, ValueKind = binding.Field.ValueKind,
                    FieldCode = binding.Field.Code, FieldName = binding.Field.Name, DeviceIdentity = binding.DeviceIdentity,
                    SourceId = binding.SourceId, Status = status, ObservedAt = at };
            datasets[binding.RequestAddress] = new(binding.RequestAddress, status,
                snapshot.CommittedGeneration, value is null ? 0 : at.UtcTicks, snapshot.WorkspaceIdentity,
                snapshot.ConfigurationGeneration, snapshot.CatalogGeneration, snapshot.CommittedGeneration)
            { LastAttemptAtUtcTicks = at.UtcTicks, LastSuccessAtUtcTicks = value is null ? 0 : at.UtcTicks,
                FailureCode = value is null ? status == SamplingObservationStatus.Unsupported
                    ? "control-setting-read-unsupported" : "control-setting-read-unavailable" : null };
        }
        return prepared with { Items = items, Datasets = datasets };
    }

    /// <summary>Apply only this collection's prepared managed control facts to the newly
    /// committed native frame. Native values and their observation times remain authoritative.</summary>
    internal static HardwareMetricSnapshot ApplyPreparedFields(HardwareMetricSnapshot snapshot, HardwareMetricSnapshot prepared)
    {
        var controls = prepared.FieldBindings.Values
            .Where(static binding => binding.ControlTarget is not null
                && WindowsMetricControlBindings.TryFind(binding) is not null).ToArray();
        if (controls.Length == 0) return snapshot;
        var bindings = new Dictionary<string, MetricDeviceFieldBinding>(snapshot.FieldBindings, StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, MetricReadingValue>(snapshot.Items, StringComparer.OrdinalIgnoreCase);
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(snapshot.Datasets, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in controls)
        {
            var address = binding.RequestAddress;
            bindings[address] = binding;
            items.Remove(address);
            datasets.Remove(address);
            if (prepared.Items.TryGetValue(address, out var item)) items[address] = item;
            if (prepared.Datasets.TryGetValue(address, out var observation))
                datasets[address] = observation with {
                    SourceGeneration = snapshot.CommittedGeneration,
                    WorkspaceIdentity = snapshot.WorkspaceIdentity,
                    ConfigurationGeneration = snapshot.ConfigurationGeneration,
                    CatalogGeneration = snapshot.CatalogGeneration,
                    CommittedGeneration = snapshot.CommittedGeneration
                };
        }
        return snapshot with { FieldBindings = bindings, Items = items, Datasets = datasets };
    }

    private HardwareMetricSnapshot AddBindings(HardwareMetricSnapshot snapshot)
    {
        var bindings = snapshot.FieldBindings.Where(pair => WindowsMetricControlBindings.TryFind(pair.Value) is null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in Volatile.Read(ref declarations).Values)
            bindings.Add(declaration.Binding.RequestAddress, declaration.Binding);
        return snapshot with { FieldBindings = bindings };
    }
    private Declaration? Find(MetricDeviceFieldBinding binding)
        => WindowsMetricControlBindings.TryFind(binding) is not null
            && Volatile.Read(ref declarations).TryGetValue(binding.RequestAddress, out var found)
            && found.Binding.DeviceIdentity == binding.DeviceIdentity && found.Binding.AdapterKey == binding.AdapterKey
            && found.Binding.ControlTarget == binding.ControlTarget ? found : null;

    public async ValueTask<FieldWriteResult> WriteAsync(FieldReference field, MetricDeviceFieldBinding binding,
        FieldValue value, CancellationToken token = default)
    {
        var validation = ValidateWrite(field, binding, value, token);
        if (validation.Failure is { } failure) return failure;
        var declaration = validation.Declaration!;
        var entry = declaration.Entry;
        var adapter = validation.Adapter;
        var number = Numeric(value);
        var rawId = binding.ControlTarget!.RawObjectId!;
        var contract = validation.Contract!;
        switch (entry.Family)
        {
            case WindowsMetricControlFamily.IntelCpu:
                var intel = await resources.IntelCpuControl!.WriteAsync(new(rawId), IntelCapability(entry, contract),
                    new(entry.RawCapability, number, entry.RawUnit), token).ConfigureAwait(false);
                return new(field, intel.Status, intel.Message) { Evidence = intel.Status == "applied" ? new(entry.Evidence) : null };
            case WindowsMetricControlFamily.AmdCpu:
                var amdField = AmdCpuControlFormatter.Field(entry.RawCapability)!.Value;
                var amd = await resources.AmdCpuControl!.WriteAsync(new(rawId, true), AmdCapability(entry, contract),
                    new(amdField, number, entry.RawUnit), token).ConfigureAwait(false);
                return new(field, amd.Status == AmdCpuControlStatus.Applied ? "applied" : "failed", amd.Message)
                    { Evidence = amd.Status == AmdCpuControlStatus.Applied ? new(entry.Evidence) : null };
            case WindowsMetricControlFamily.AmdDesktopGpu:
                var gpuField = AmdDesktopGpuControlFormatter.Field(entry.RawCapability)!.Value;
                var gpu = await resources.AmdDesktopGpuControl!.WriteAsync(DesktopTarget(binding, entry, adapter!), gpuField,
                    new(number, value.Boolean), token).ConfigureAwait(false);
                return new(field, gpu.Status == AmdDesktopGpuControlStatus.Applied ? "applied"
                    : gpu.Status == AmdDesktopGpuControlStatus.Unsupported ? "unsupported" : "failed", gpu.Error)
                    { Evidence = gpu.Status == AmdDesktopGpuControlStatus.Applied ? new(entry.Evidence) : null };
            default:
                var integratedField = IntegratedGpuControlFormatter.Field(entry.RawCapability)!.Value;
                var integrated = await resources.IntegratedGpuControl!.WriteAsync(IntegratedTarget(binding, entry),
                    IntegratedCapability(entry, contract), new(integratedField, number, entry.RawUnit), token).ConfigureAwait(false);
                return new(field, integrated.Status == IntegratedGpuControlStatus.Applied ? "applied" : "failed", integrated.Message)
                    { Evidence = integrated.Status == IntegratedGpuControlStatus.Applied
                        ? new(integrated.ReadbackVerified ? FieldWriteEvidence.SettingReadbackTolerance : FieldWriteEvidence.AcceptedOnly) : null };
        }
    }

    /// <summary>Host uses its original permission/policy/write layer callback after
    /// exact fresh identity and range checks. Caller keeps the same original gate.
    /// Callback supplies its real receipt; this method never invents verification.</summary>
    public async ValueTask<FieldWriteResult> ExecuteBoundWriteAsync(FieldReference field, MetricDeviceFieldBinding binding,
        FieldValue value, Func<CancellationToken, ValueTask<FieldWriteResult>> write, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var validation = ValidateWrite(field, binding, value, token);
        return validation.Failure ?? await write(token).ConfigureAwait(false);
    }

    /// <summary>Explicit restoration through the same original resource/store;
    /// configured capability IDs are scoped by the caller to this exact device.
    /// No subscription or canonical layer baseline is created or erased here.</summary>
    public async ValueTask<bool> RestoreAsync(MetricDeviceFieldBinding binding,
        IReadOnlySet<string> stillConfiguredCapabilities, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stillConfiguredCapabilities);
        token.ThrowIfCancellationRequested();
        var known = Find(binding);
        if (known is null || !known.Entry.Implemented
            || !ResolveFresh(binding, known.Entry, CaptureFacts(), out var adapter)) return false;
        var fresh = Probe(binding, known.Entry, adapter, token);
        if (!fresh.CanWrite) return false;
        var entry = known.Entry;
        var rawId = binding.ControlTarget!.RawObjectId!;
        switch (entry.Family)
        {
            case WindowsMetricControlFamily.IntelCpu:
                return await resources.IntelCpuControl!.RestoreAsync(new(rawId), IntelCapability(entry, fresh.Contract),
                    stillConfiguredCapabilities, token).ConfigureAwait(false);
            case WindowsMetricControlFamily.AmdCpu:
                return await resources.AmdCpuControl!.RestoreAsync(new(rawId, true), AmdCapability(entry, fresh.Contract), token).ConfigureAwait(false);
            case WindowsMetricControlFamily.AmdDesktopGpu:
                var configured = stillConfiguredCapabilities.Select(AmdDesktopGpuControlFormatter.Field)
                    .Where(field => field.HasValue).Select(field => field!.Value).ToHashSet();
                return await resources.AmdDesktopGpuControl!.RestoreAsync(DesktopTarget(binding, entry, adapter!),
                    AmdDesktopGpuControlFormatter.Field(entry.RawCapability)!.Value, configured, token).ConfigureAwait(false);
            default:
                return await resources.IntegratedGpuControl!.RestoreAsync(IntegratedTarget(binding, entry),
                    IntegratedCapability(entry, fresh.Contract), token).ConfigureAwait(false);
        }
    }

    private (Declaration? Declaration, WindowsGpuAdapter? Adapter, FieldWriteContract? Contract, FieldWriteResult? Failure)
        ValidateWrite(FieldReference field, MetricDeviceFieldBinding binding, FieldValue value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var known = Find(binding);
        if (known is null || field.DeviceIdentity != binding.DeviceIdentity || field.FieldName != binding.Field.Name)
            return (null, null, null, new(field, "unsupported", "control-field-not-discovered"));
        if (!known.Entry.Implemented) return (null, null, null, new(field, "unsupported", "control-field-not-implemented"));
        if (!value.Matches(binding.Field.ValueKind)) return (null, null, null, new(field, "failed", "control-field-payload-invalid"));
        if (!ResolveFresh(binding, known.Entry, CaptureFacts(), out var adapter))
            return (null, null, null, new(field, "failed", "control-field-identity-changed-or-ambiguous"));
        var fresh = Probe(binding, known.Entry, adapter, token);
        if (!fresh.CanWrite) return (null, null, null, new(field, "unsupported", fresh.Reason));
        var numeric = Numeric(value);
        var contract = fresh.Contract;
        if (value.Boolean is null && (numeric is not { } number || contract.Minimum is not { } minimum
            || contract.Maximum is not { } maximum || contract.Step is not > 0
            || number < minimum || number > maximum
            || Math.Abs((number - minimum) / contract.Step.Value - Math.Round((number - minimum) / contract.Step.Value)) > 1e-8))
            return (null, null, null, new(field, "failed", "control-field-range-or-step-invalid"));
        token.ThrowIfCancellationRequested();
        return (fresh, adapter, contract, null);
    }

    private Declaration Probe(MetricDeviceFieldBinding binding, WindowsMetricControlBinding entry, WindowsGpuAdapter? adapter, CancellationToken token = default)
    {
        if (!entry.Implemented) return new(binding, entry, false, false, "control-field-not-implemented", entry.DeclaredRange);
        var rawId = binding.ControlTarget!.RawObjectId!;
        try
        {
            switch (entry.Family)
            {
                case WindowsMetricControlFamily.IntelCpu when resources.IntelCpuControl is { } intel:
                    var i = intel.Probe(new(rawId), IntelCapability(entry, entry.DeclaredRange));
                    return new(binding, entry, i.IsReadable, i.CanWrite, i.Reason,
                        i.Range is { } ir ? WithRange(entry, ir.Minimum, ir.Maximum, ir.Step, ir.DefaultValue) : entry.DeclaredRange);
                case WindowsMetricControlFamily.AmdCpu when resources.AmdCpuControl is { } amd:
                    var a = amd.Probe(new(rawId, true), AmdCapability(entry, entry.DeclaredRange));
                    return new(binding, entry, (a.CanWrite || a.CanRead) && !entry.ReadDependencies.IsEmpty, a.CanWrite, a.Reason,
                        a.Range is { } ar ? WithRange(entry, ar.Minimum, ar.Maximum, ar.Step, ar.DefaultValue) : entry.DeclaredRange);
                case WindowsMetricControlFamily.AmdDesktopGpu when resources.AmdDesktopGpuControl is { } desktop:
                    var g = desktop.Probe(DesktopTarget(binding, entry, adapter!), AmdDesktopGpuControlFormatter.Field(entry.RawCapability)!.Value);
                    var modeAdmitted = ModeAdmitted(entry.RawCapability, g.GfxMode);
                    return new(binding, entry, g.CanWrite && modeAdmitted, g.CanWrite && modeAdmitted,
                        modeAdmitted ? g.Error : "control-field-gfx-mode-unavailable",
                        g.Range is { } gr ? WithRange(entry, gr.Minimum, gr.Maximum, gr.Step, null) : entry.DeclaredRange);
                case WindowsMetricControlFamily.IntelIntegratedGpu or WindowsMetricControlFamily.AmdIntegratedGpu
                    when resources.IntegratedGpuControl is { } integrated:
                    var v = integrated.Probe(IntegratedTarget(binding, entry), IntegratedCapability(entry, entry.DeclaredRange), token);
                    return new(binding, entry, v.CanWrite || v.CanRead, v.CanWrite, v.Reason,
                        v.Range is { } vr ? WithRange(entry, vr.Minimum, vr.Maximum, vr.Step, vr.DefaultValue) : entry.DeclaredRange);
                default: return new(binding, entry, false, false, "control-resource-not-supplied", entry.DeclaredRange);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new(binding, entry, false, false, error.Message, entry.DeclaredRange); }
    }
    private static bool ModeAdmitted(string capability, AdlxGfxMode? mode) => capability switch
    {
        "gpu.core-clock-minimum" => mode is AdlxGfxMode.Absolute or AdlxGfxMode.Offset,
        "gpu.core-clock-maximum" or "gpu.core-voltage" => mode == AdlxGfxMode.Absolute,
        "gpu.core-clock-offset" or "gpu.core-voltage-offset" => mode == AdlxGfxMode.Offset,
        _ => true
    };
    private static FieldWriteContract WithRange(WindowsMetricControlBinding entry, double min, double max, double step, double? baseline)
        => entry.DeclaredRange with { Minimum = min, Maximum = max, Step = step, DeclaredDefault = baseline,
            RangeOrigin = entry.Family == WindowsMetricControlFamily.AmdDesktopGpu ? "original-adlx-tuning-range" : entry.DeclaredRange.RangeOrigin };
    private static double? Numeric(FieldValue value)
        => value.Number ?? (value.Integer is { FitsSigned32: true } integer ? integer.ToSigned64() : (double?)null);
    private static FieldValue? Value(WindowsMetricControlBinding entry, double? number, bool? toggle = null)
    {
        if (entry.Definition.ValueKind == "boolean") return toggle is { } flag ? new(Boolean: flag) : null;
        if (number is not { } numeric || !double.IsFinite(numeric)) return null;
        return entry.Definition.ValueKind == "signed64"
            ? numeric == Math.Truncate(numeric) && numeric >= int.MinValue && numeric <= int.MaxValue
                ? new(Integer: MetricInteger.FromSigned64((int)numeric)) : null : new(Number: numeric);
    }
    private async ValueTask<FieldValue?> ReadRawAsync(MetricDeviceFieldBinding binding, WindowsMetricControlBinding entry,
        WindowsGpuAdapter? adapter, CancellationToken token)
    {
        var rawId = binding.ControlTarget!.RawObjectId!;
        var range = Find(binding)!.Contract;
        switch (entry.Family)
        {
            case WindowsMetricControlFamily.IntelCpu:
                return Value(entry, (await resources.IntelCpuControl!.ReadAsync(new(rawId), IntelCapability(entry, range), token).ConfigureAwait(false))?.Number);
            case WindowsMetricControlFamily.AmdCpu:
                return Value(entry, (await resources.AmdCpuControl!.ReadAsync(new(rawId, true), AmdCapability(entry, range), token).ConfigureAwait(false))?.Number);
            case WindowsMetricControlFamily.AmdDesktopGpu:
                var read = await resources.AmdDesktopGpuControl!.ReadAsync(DesktopTarget(binding, entry, adapter!),
                    AmdDesktopGpuControlFormatter.Field(entry.RawCapability)!.Value, token).ConfigureAwait(false);
                return read is null || !ModeAdmitted(entry.RawCapability, read.GfxMode) ? null : Value(entry, read.Number, read.Toggle);
            default:
                return Value(entry, (await resources.IntegratedGpuControl!.ReadAsync(IntegratedTarget(binding, entry),
                    IntegratedCapability(entry, range), token).ConfigureAwait(false))?.Number);
        }
    }

    private Facts CaptureFacts()
    {
        CpuDomain? cpu = null; WindowsGpuAdapterInventoryRead? inventory = null; var fixedChassis = false;
        try { cpu = SingleCpuDomain(resources.ControlReadCpuIdentity(), resources.ControlReadCpuTopology()); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        try { inventory = readInventory(); } catch (Exception error) when (error is not OperationCanceledException) { }
        try { fixedChassis = resources.ControlIsFixedChassis(); } catch (Exception error) when (error is not OperationCanceledException) { }
        return new(cpu, inventory, fixedChassis);
    }
    private static CpuDomain? SingleCpuDomain(CpuIdentityInformation? identity, CpuOsTopologyInformation topology)
    {
        if (identity is null || identity.Vendor is not ("GenuineIntel" or "AuthenticAMD")
            || topology.Packages is not { Count: 1 } packages || topology.PhysicalCores is not { Count: > 0 } cores
            || topology.ThreadCpuid is not { Count: > 0 } threads) return null;
        var ids = packages[0].LogicalProcessorIds.Order().ToArray();
        var coreIds = cores.SelectMany(core => core.LogicalProcessorIds).Order().ToArray();
        if (ids.Length == 0 || ids.Any(id => id < 0) || ids.Distinct().Count() != ids.Length
            || !ids.SequenceEqual(coreIds) || cores.Any(core => core.LogicalProcessorIds.Count == 0)
            || !ids.SequenceEqual(threads.Select(thread => thread.LogicalProcessorId).Order())
            || threads.Any(thread => thread.Data is not { } data || data.Identity.Vendor != identity.Vendor
                || data.Identity.Family != identity.Family || data.Identity.Model != identity.Model
                || data.Identity.Stepping != identity.Stepping)) return null;
        var members = ids.Select(static id => new MetricProcessorAddress(
            checked((ushort)(id / 64)), checked((byte)(id % 64)))).ToArray();
        var displayName = string.IsNullOrWhiteSpace(identity.Brand) ? null : identity.Brand.Trim();
        var device = new MetricDeviceDescriptor("cpu_platform", MetricDeviceAddress.CpuPlatform(members), displayName, 0);
        return new("host-cpu-control|" + identity.Vendor + "|" + identity.Family + ":" + identity.Model + ":" + identity.Stepping
            + "|" + identity.Brand + "|" + packages[0].DeviceIdentity + "|" + string.Join(',', ids), identity.Vendor, device);
    }

    private static MetricDeviceDescriptor GpuDevice(WindowsGpuAdapter adapter, bool fanTuningGroup)
    {
        var pnp = adapter.PhysicalIdentities
            .OrderBy(static identity => identity.PnpInstanceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static identity => identity.PhysicalAdapterIndex)
            .Select(static identity => identity.PnpInstanceId)
            .ToArray();
        var luid = WindowsGpuAdapterIdentity.Pack(adapter.Luid);
        var address = fanTuningGroup
            ? MetricDeviceAddress.GpuFanTuningGroup(luid, pnp)
            : MetricDeviceAddress.GpuAdapter(luid, pnp);
        return new(fanTuningGroup ? "gpu_fan_tuning_group" : "gpu_adapter", address,
            string.IsNullOrWhiteSpace(adapter.Name) ? null : adapter.Name.Trim(), adapter.Index);
    }
    private static bool Complete(WindowsGpuAdapterInventoryRead? inventory)
        => inventory is not null && inventory.Status == SamplingObservationStatus.Current && inventory.Generation > 0
            && inventory.ObservedAtUtcTicks > 0 && inventory.SkippedCount == 0 && inventory.OverflowCount == 0
            && inventory.ObservedCount == inventory.Adapters.Count
            && inventory.Adapters.Select(adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid)).Distinct().Count() == inventory.Adapters.Count;
    private static bool AdmittedGpu(WindowsMetricControlBinding entry, WindowsGpuAdapter adapter, Facts facts)
    {
        if (adapter.IsSoftware || adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current
            || adapter.PhysicalIdentities.Count != 1 || WindowsGpuAdapterIdentity.Pack(adapter.Luid) == 0
            || string.IsNullOrEmpty(WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter))) return false;
        return entry.Family switch
        {
            WindowsMetricControlFamily.AmdDesktopGpu => facts.FixedChassis && adapter.VendorId == 0x1002 && adapter.Kind == WindowsGpuAdapterKind.Dedicated,
            WindowsMetricControlFamily.IntelIntegratedGpu => adapter.VendorId == 0x8086 && adapter.Kind == WindowsGpuAdapterKind.Integrated,
            WindowsMetricControlFamily.AmdIntegratedGpu => facts.Cpu?.Vendor == "AuthenticAMD" && adapter.VendorId == 0x1002 && adapter.Kind == WindowsGpuAdapterKind.Integrated,
            _ => false
        };
    }
    private bool ResolveFresh(MetricDeviceFieldBinding binding, WindowsMetricControlBinding entry, Facts facts, out WindowsGpuAdapter? adapter)
    {
        adapter = null;
        if (binding.ControlTarget is not { RawObjectId: not null } target) return false;
        if (entry.Family is WindowsMetricControlFamily.IntelCpu or WindowsMetricControlFamily.AmdCpu)
            return facts.Cpu is { } cpu && cpu.Identity == target.HostCpuIdentity && cpu.Identity == binding.DeviceIdentity;
        if (!Complete(facts.Inventory)) return false;
        var admitted = facts.Inventory!.Adapters.Where(item => AdmittedGpu(entry, item, facts)).ToArray();
        if (entry.Family == WindowsMetricControlFamily.AmdIntegratedGpu && (admitted.Length != 1
            || facts.Cpu?.Identity != target.HostCpuIdentity)) return false;
        var matches = admitted.Where(item => WindowsGpuAdapterIdentity.Pack(item.Luid) == binding.AdapterKey
            && WindowsHardwareMetricTopologySource.CreateExactGpuKey(item) == target.GpuDeviceIdentity
            && string.Equals(item.PhysicalIdentities.Single().PnpInstanceId, target.GpuPnp, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return false;
        adapter = matches[0]; return true;
    }
    private static IntelCpuControlCapability IntelCapability(WindowsMetricControlBinding entry, FieldWriteContract c)
        => new(entry.RawCapability, c.Minimum is { } min && c.Maximum is { } max && c.Step is { } step
            ? new(min, max, step, entry.RawUnit, c.DeclaredDefault) : null);
    private static AmdCpuControlCapability AmdCapability(WindowsMetricControlBinding entry, FieldWriteContract c)
        => new(AmdCpuControlFormatter.Field(entry.RawCapability)!.Value,
            c.Minimum is { } min && c.Maximum is { } max && c.Step is { } step ? new(min, max, step, entry.RawUnit, c.DeclaredDefault) : null);
    private static IntegratedGpuControlCapability IntegratedCapability(WindowsMetricControlBinding entry, FieldWriteContract c)
        => new(IntegratedGpuControlFormatter.Field(entry.RawCapability)!.Value,
            c.Minimum is { } min && c.Maximum is { } max && c.Step is { } step ? new(min, max, step, entry.RawUnit, c.DeclaredDefault) : null);
    private static IntegratedGpuControlTarget IntegratedTarget(MetricDeviceFieldBinding binding, WindowsMetricControlBinding entry)
        => new(binding.ControlTarget!.RawObjectId!, entry.Family == WindowsMetricControlFamily.IntelIntegratedGpu
            ? IntegratedGpuControlRoute.IntelIntegrated : IntegratedGpuControlRoute.AmdIntegrated, binding.AdapterKey);
    private static AmdDesktopGpuControlTarget DesktopTarget(MetricDeviceFieldBinding binding, WindowsMetricControlBinding entry, WindowsGpuAdapter adapter)
        => new(adapter.Index, AmdDesktopGpuControlRoute.WindowsAmdFixedDiscrete,
            entry.Definition.Resource == MetricResource.Fan ? AmdDesktopGpuControlObjectKind.Fan : AmdDesktopGpuControlObjectKind.Gpu,
            binding.ControlTarget!.GpuPnp);
}
