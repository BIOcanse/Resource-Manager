using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed record MetricReadingSelection(
    IReadOnlyDictionary<ulong, MetricReadingSource> Fields,
    IReadOnlyDictionary<string, ulong> LegacyHandles);

// Small binding/capability metadata owned by the existing capture gate. No
// numeric cache, collector, timer, native allocation or ledger registration.
internal sealed class MetricReadingSourceTracker
{
    private readonly Dictionary<ulong, Capability> capabilities = [];
    private readonly Dictionary<ulong, string> quantities = [];
    private readonly Dictionary<string, Capability> projectedCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> sourceCapabilityEpochs = new(StringComparer.Ordinal);
    private ulong workspace;
    private ulong configuration;
    private ulong catalogGeneration;
    private string? integratedGpu;

    private sealed record Capability(ulong Epoch, string Availability);
    private sealed record Candidate(NativeMetricSnapshotRuleCatalogBinding Binding,
        string Device, MetricPhysicalReadingRule Rule, string Availability, string ReadingId);

    public void Observe(NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity,
        IEnumerable<NativeMetricSnapshotSourceCompletion> completions)
    {
        EnsureGeneration(frame, workspaceIdentity);
        var sources = frame.Sources.ToDictionary(static row => row.SourceHandle);
        foreach (var binding in frame.Catalog.MetricBindings.Values)
            if (sources.TryGetValue(binding.SourceHandle, out var sourceForBinding))
                sourceCapabilityEpochs[binding.SourceId] = sourceForBinding.CapabilityGeneration;
        foreach (var completion in completions)
        {
            if (!sources.TryGetValue(completion.SourceHandle, out var source)
                || source.SourceIncarnation != completion.SourceIncarnation
                || source.SourceObservationSequence != completion.SourceObservationSequence) continue;
            if (completion.IntegratedGpuDeviceIdentity is { } gpu) integratedGpu = gpu;
            foreach (var binding in frame.Catalog.MetricBindings.Values.Where(row => row.SourceHandle == completion.SourceHandle))
            {
                var existing = capabilities.GetValueOrDefault(binding.MetricHandle);
                if (existing?.Epoch != source.CapabilityGeneration)
                {
                    capabilities.Remove(binding.MetricHandle);
                    quantities.Remove(binding.MetricHandle);
                }
                if (completion.Status == NativeMetricSnapshotSourceStatus.Unsupported)
                    capabilities[binding.MetricHandle] = new(source.CapabilityGeneration, "unsupported");
            }
            foreach (var pair in completion.ReadingQuantities) quantities[pair.Key] = pair.Value;
            foreach (var handle in completion.SupportedMetricHandles)
                SetCapability(handle, "available");
            foreach (var observation in completion.Observations)
            {
                if (observation.Status == (uint)NativeMetricSnapshotObservationStatus.Unsupported)
                    SetCapability(observation.MetricHandle, "unsupported");
                else if (observation.Status == (uint)NativeMetricSnapshotObservationStatus.Current)
                    SetCapability(observation.MetricHandle, "available");
                // Missing, skipped and unavailable values do not change support or preference.
            }
            // Counter-based sources (including Windows CPU) produce their value
            // inside the original native commit, not in completion.Observations.
            // Learn from that same accepted source generation as well.
            foreach (var metric in frame.Metrics)
                if (metric.SourceHandle == source.SourceHandle && metric.SourceGeneration == source.SourceGeneration)
                {
                    if (metric.Status == (uint)NativeMetricSnapshotMetricStatus.Current)
                        SetCapability(metric.MetricHandle, "available");
                    else if (metric.Status == (uint)NativeMetricSnapshotMetricStatus.Unsupported)
                        SetCapability(metric.MetricHandle, "unsupported");
                }
            void SetCapability(ulong handle, string availability)
            {
                if (frame.Catalog.MetricBindings.TryGetValue(handle, out var binding)
                    && binding.SourceHandle == completion.SourceHandle)
                    capabilities[handle] = new(source.CapabilityGeneration, availability);
            }
        }
        foreach (var aliases in frame.Catalog.MetricBindings.Values.Where(static row => row.SourceId == "amd.adlx"
            && row.FieldCode is MetricFieldCode.GpuMemoryUsed or MetricFieldCode.AdlxDedicatedMemoryMb)
            .GroupBy(static row => row.DeviceIdentity, StringComparer.Ordinal))
        {
            var fact = aliases.Select(row => capabilities.GetValueOrDefault(row.MetricHandle))
                .FirstOrDefault(static c => c?.Availability == "available");
            if (fact is not null) foreach (var alias in aliases) capabilities[alias.MetricHandle] = fact;
        }
        // These are two public presentations of one exact SMU reading, not
        // substitute values. Establish support, but never copy numeric data.
        if (integratedGpu is not null)
        {
            var aliases = frame.Catalog.MetricBindings.Values.Where(row => row.SourceId == "amd.smu"
                && (row.FieldCode == MetricFieldCode.CpuIgpuTemperature
                    || row.DeviceIdentity == integratedGpu && row.FieldCode == MetricFieldCode.GpuTemperature)).ToArray();
            var supported = aliases.Select(row => capabilities.GetValueOrDefault(row.MetricHandle))
                .FirstOrDefault(static fact => fact?.Availability == "available");
            if (supported is not null)
                foreach (var alias in aliases) capabilities[alias.MetricHandle] = supported;
        }
    }

    public MetricReadingSelection Select(NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity)
    {
        EnsureGeneration(frame, workspaceIdentity);
        var sources = frame.Sources.ToDictionary(static row => row.SourceHandle);
        var nvmeDevices = frame.Catalog.MetricBindings.Values
            .Where(static row => row.FieldCode == MetricFieldCode.NvmeCompositeTemperature)
            .Select(static row => row.DeviceIdentity).ToHashSet(StringComparer.Ordinal);
        var candidates = frame.Catalog.MetricBindings.Values.Where(static row => row.Field is not null).Select(row =>
        {
            var fact = capabilities.GetValueOrDefault(row.MetricHandle);
            var currentCapability = sources.TryGetValue(row.SourceHandle, out var source)
                && fact?.Epoch == source.CapabilityGeneration;
            var rule = MetricFieldRegistry.ReadingRule(row.Field!, row.SourceId,
                nvmeDevices.Contains(row.DeviceIdentity), currentCapability ? quantities.GetValueOrDefault(row.MetricHandle) : null);
            var device = row.FieldCode == MetricFieldCode.CpuIgpuTemperature && row.SourceId == "amd.smu"
                && integratedGpu is not null ? integratedGpu : row.DeviceIdentity;
            if (row.FieldCode == MetricFieldCode.CpuIgpuTemperature && integratedGpu is null)
                rule = rule with { Quantity = "cpu.apu_temperature_unmapped" };
            var available = currentCapability ? fact!.Availability : "unverified";
            var id = ReadingId(device, rule.Quantity);
            return new Candidate(row, device, rule, available, id);
        }).ToArray();
        var preferred = candidates.GroupBy(static row => row.ReadingId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Where(static row => row.Availability == "available")
                .OrderBy(static row => row.Rule.SourceRank).ThenBy(static row => row.Rule.AliasRank)
                .ThenBy(static row => row.Binding.Field!.Name, StringComparer.Ordinal)
                .ThenBy(static row => row.Binding.DeviceIdentity, StringComparer.Ordinal).FirstOrDefault(), StringComparer.Ordinal);
        var fields = candidates.ToDictionary(static row => row.Binding.MetricHandle, row =>
        {
            var best = preferred[row.ReadingId];
            return new MetricReadingSource(row.ReadingId, row.Rule.Quantity, row.Rule.SourceRank,
                row.Availability, best?.Binding.MetricHandle == row.Binding.MetricHandle,
                best?.Binding.DeviceIdentity, best?.Binding.Field!.Name);
        });
        var legacy = frame.Catalog.LegacyMetricHandles.ToDictionary(static pair => pair.Key,
            static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.Where(row => LegacyAddress(row.Binding).Length != 0)
                     .GroupBy(row => LegacyAddress(row.Binding), StringComparer.OrdinalIgnoreCase))
        {
            // Resolve through the same preferred reading metadata used by the UI.
            var best = group.Select(row => preferred[row.ReadingId]).Where(static row => row is not null)
                .OrderBy(static row => row!.Rule.SourceRank).ThenBy(row => sources[row!.Binding.SourceHandle].Priority)
                .ThenBy(static row => row!.Binding.MetricHandle).FirstOrDefault();
            if (best is null) legacy.Remove(group.Key);
            else legacy[group.Key] = best.Binding.MetricHandle;
        }
        return new(fields, legacy);
    }

    public HardwareMetricSnapshot ApplyProjectedFields(HardwareMetricSnapshot snapshot)
    {
        var bindings = new Dictionary<string, MetricDeviceFieldBinding>(snapshot.FieldBindings, StringComparer.OrdinalIgnoreCase);
        foreach (var key in projectedCapabilities.Keys.Where(key => !bindings.ContainsKey(key)).ToArray()) projectedCapabilities.Remove(key);
        foreach (var (address, binding) in snapshot.FieldBindings)
        {
            if (binding.ControlTarget is not null || (binding.Field.Reading is not null
                && binding.Field.Code != MetricFieldCode.NpuDeviceEngineBusyPercent)) continue;
            var epoch = sourceCapabilityEpochs.GetValueOrDefault(binding.SourceId);
            if (projectedCapabilities.GetValueOrDefault(address)?.Epoch != epoch) projectedCapabilities.Remove(address);
            var status = snapshot.Datasets.GetValueOrDefault(address)?.Status;
            if (status == SamplingObservationStatus.Current) projectedCapabilities[address] = new(epoch, "available");
            else if (status == SamplingObservationStatus.Unsupported || binding.Field.Reading?.SourceAvailability == "unsupported")
                projectedCapabilities[address] = new(epoch, "unsupported");
            var availability = projectedCapabilities.GetValueOrDefault(address)?.Availability ?? "unverified";
            var rule = MetricFieldRegistry.ReadingRule(binding.Field, binding.SourceId, nvmeDevice: false);
            var id = ReadingId(binding.DeviceIdentity, rule.Quantity);
            bindings[address] = binding with { Field = binding.Field with { Reading = new(id, rule.Quantity,
                rule.SourceRank, availability, availability == "available",
                availability == "available" ? binding.DeviceIdentity : null,
                availability == "available" ? binding.Field.Name : null) } };
        }
        return snapshot with { FieldBindings = bindings };
    }

    public MetricSampleRequest ExpandLegacyRequest(MetricSampleRequest request,
        NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity)
    {
        if (request.IsAll || request.IsCatalogProbe) return request;
        var selection = Select(frame, workspaceIdentity);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in request.Ids)
        {
            var bindings = frame.Catalog.MetricBindings.Values
                .Where(row => string.Equals(LegacyAddress(row), id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bindings.Length == 0) { expanded.Add(id); continue; }
            if (selection.LegacyHandles.TryGetValue(id, out var handle))
                expanded.Add(frame.Catalog.MetricIds[handle]);
            // Unverified support is probed through the same requested target
            // and owner; no new sampler or polling loop. Transient failures
            // remain subject to the existing source cooldown.
            var readingIds = bindings.Select(row => selection.Fields.GetValueOrDefault(row.MetricHandle)?.Id)
                .Where(static id => id is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var binding in frame.Catalog.MetricBindings.Values
                         .Where(row => readingIds.Contains(selection.Fields.GetValueOrDefault(row.MetricHandle)?.Id)))
                if (selection.Fields.GetValueOrDefault(binding.MetricHandle)?.SourceAvailability == "unverified")
                    expanded.Add(binding.MetricId);
        }
        var result = request.IncludesAllGpuCoreMetrics
            ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(expanded) : MetricSampleRequest.ForIds(expanded);
        return request.IncludesCatalog ? result.WithCatalog() : result;
    }

    private void EnsureGeneration(NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity)
    {
        if (workspace == workspaceIdentity && configuration == frame.Header.ConfigurationGeneration
            && catalogGeneration == frame.Header.CatalogGeneration) return;
        capabilities.Clear(); quantities.Clear(); projectedCapabilities.Clear(); sourceCapabilityEpochs.Clear(); integratedGpu = null;
        workspace = workspaceIdentity;
        configuration = frame.Header.ConfigurationGeneration;
        catalogGeneration = frame.Header.CatalogGeneration;
    }

    private static string ReadingId(string device, string quantity)
        => "reading-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device + "\0" + quantity)));

    // Component addresses are selected here, alongside the public reading
    // metadata, rather than by dashboard, scheduler or UI vendor branches.
    internal static string LegacyAddress(NativeMetricSnapshotRuleCatalogBinding binding)
    {
        if (binding.LegacyMetricId.Length != 0) return binding.LegacyMetricId;
        if (binding.SourceId == "amd.smu" && binding.FieldCode == MetricFieldCode.GpuTemperature && binding.DisplayIndex >= 0)
            return $"gpu.{binding.DisplayIndex}.temperature";
        if (binding.SourceId == "intel.igcl" && binding.DisplayIndex >= 0)
            return binding.FieldCode switch
            {
                MetricFieldCode.IgclGpuTemperature => $"gpu.{binding.DisplayIndex}.temperature",
                MetricFieldCode.IgclGpuIntervalPower => $"gpu.{binding.DisplayIndex}.power",
                MetricFieldCode.IgclGpuVoltage => $"gpu.{binding.DisplayIndex}.coreVoltage",
                _ => string.Empty
            };
        // The legacy singular CPU card refers to the first actual package;
        // individual package readings retain their own identities and values.
        if (binding.SourceId == "intel.msr" && binding.FieldCode == MetricFieldCode.IntelPackageDtsTemperature
            && binding.Device.Kind == "cpu_package" && binding.Device.Ordinal == 0)
            return "cpu.temperature";
        return string.Empty;
    }
}
