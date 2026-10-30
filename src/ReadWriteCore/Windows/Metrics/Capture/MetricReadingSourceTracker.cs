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
    private readonly Dictionary<ulong, NativeMetricSnapshotSourceOutput> sourceRows = [];
    private readonly Dictionary<(string Device, string Quantity), string> readingIds = [];
    private readonly Dictionary<string, int> preferredCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyChoice> legacyCandidates = new(StringComparer.OrdinalIgnoreCase);
    private SelectionDefinition[] selectionDefinitions = [];
    private bool selectionDefinitionsValid;
    // The previous selection is returned again, or its sources reused, when the
    // values are equal; the projection then reuses its bindings by identity.
    private MetricReadingSelection? lastSelection;
    private SelectionDefinition[]? lastSelectionDefinitions;
    private MetricReadingSource?[] selectedSources = [];
    private readonly Dictionary<string, ulong> legacyScratch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (MetricDeviceFieldBinding Source, MetricDeviceFieldBinding Projected)> projectedBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private ulong workspace;
    private ulong configuration;
    private ulong catalogGeneration;
    private string? integratedGpu;

    private sealed record Capability(ulong Epoch, string Availability);
    private readonly record struct SelectionDefinition(
        NativeMetricSnapshotRuleCatalogBinding Binding,
        string Device,
        string Quantity,
        int SourceRank,
        int AliasRank,
        string ReadingId,
        string LegacyAddress,
        bool UnmappedApu,
        bool DynamicQuantity,
        bool SharedReadingId);
    private readonly record struct Candidate(string Quantity, string Availability, string ReadingId);
    private readonly record struct LegacyChoice(bool HasPreferred, int CandidateIndex);

    public void Observe(NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity,
        IEnumerable<NativeMetricSnapshotSourceCompletion> completions)
    {
        EnsureGeneration(frame.Header, workspaceIdentity);
        LoadSourceRows(frame.Sources);
        var selectionMetadataChanged = false;
        foreach (var binding in frame.Catalog.MetricBindings.Values)
            if (sourceRows.TryGetValue(binding.SourceHandle, out var sourceForBinding))
                sourceCapabilityEpochs[binding.SourceId] = sourceForBinding.CapabilityGeneration;
        foreach (var completion in completions)
        {
            if (!sourceRows.TryGetValue(completion.SourceHandle, out var source)
                || source.SourceIncarnation != completion.SourceIncarnation
                || source.SourceObservationSequence != completion.SourceObservationSequence) continue;
            if (completion.IntegratedGpuDeviceIdentity is { } gpu
                && !string.Equals(integratedGpu, gpu, StringComparison.Ordinal))
            {
                integratedGpu = gpu;
                selectionMetadataChanged = true;
            }
            foreach (var binding in frame.Catalog.MetricBindings.Values.Where(row => row.SourceHandle == completion.SourceHandle))
            {
                var existing = capabilities.GetValueOrDefault(binding.MetricHandle);
                if (existing?.Epoch != source.CapabilityGeneration)
                {
                    capabilities.Remove(binding.MetricHandle);
                    selectionMetadataChanged |= quantities.Remove(binding.MetricHandle);
                }
                if (completion.Status == NativeMetricSnapshotSourceStatus.Unsupported)
                    capabilities[binding.MetricHandle] = new(source.CapabilityGeneration, "unsupported");
            }
            foreach (var pair in completion.ReadingQuantities)
            {
                if (quantities.TryGetValue(pair.Key, out var previous)
                    && string.Equals(previous, pair.Value, StringComparison.Ordinal)) continue;
                quantities[pair.Key] = pair.Value;
                selectionMetadataChanged = true;
            }
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
                    && binding.SourceHandle == completion.SourceHandle
                    && !(capabilities.TryGetValue(handle, out var existing)
                        && existing.Epoch == source.CapabilityGeneration
                        && string.Equals(existing.Availability, availability, StringComparison.Ordinal)))
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
        if (selectionMetadataChanged) InvalidateSelectionDefinitions();
    }

    public MetricReadingSelection Select(NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity)
    {
        return Select(frame.Catalog, frame.Header, frame.Sources, workspaceIdentity);
    }

    private MetricReadingSelection Select(NativeMetricSnapshotCatalogProjection catalog,
        NativeMetricSnapshotSnapshotHeader header,
        IReadOnlyList<NativeMetricSnapshotSourceOutput> sources,
        ulong workspaceIdentity)
    {
        EnsureGeneration(header, workspaceIdentity);
        LoadSourceRows(sources);
        EnsureSelectionDefinitions(catalog);
        preferredCandidates.Clear();
        legacyCandidates.Clear();
        for (var i = 0; i < selectionDefinitions.Length; i++)
        {
            var definition = selectionDefinitions[i];
            var candidate = CreateCandidate(definition);
            if (definition.SharedReadingId && candidate.Availability == "available")
            {
                if (!preferredCandidates.TryGetValue(candidate.ReadingId, out var current)
                    || IsPreferredCandidate(i, current))
                    preferredCandidates[candidate.ReadingId] = i;
            }
        }

        var known = ReferenceEquals(lastSelectionDefinitions, selectionDefinitions) ? lastSelection : null;
        if (selectedSources.Length != selectionDefinitions.Length)
            selectedSources = new MetricReadingSource?[selectionDefinitions.Length];
        var fieldsReused = known is not null;
        for (var i = 0; i < selectionDefinitions.Length; i++)
        {
            var definition = selectionDefinitions[i];
            var candidate = CreateCandidate(definition);
            var bestIndex = i;
            var hasPreferred = definition.SharedReadingId
                ? preferredCandidates.TryGetValue(candidate.ReadingId, out bestIndex)
                : candidate.Availability == "available";
            var best = hasPreferred ? selectionDefinitions[bestIndex] : default;
            var isPreferred = hasPreferred && best.Binding.MetricHandle == definition.Binding.MetricHandle;
            var preferredDevice = hasPreferred ? best.Binding.DeviceIdentity : null;
            var preferredField = hasPreferred ? best.Binding.Field!.Name : null;
            if (known is not null && known.Fields.TryGetValue(definition.Binding.MetricHandle, out var prior)
                && IsSource(prior, candidate.ReadingId, candidate.Quantity, definition.SourceRank,
                    candidate.Availability, isPreferred, preferredDevice, preferredField))
            {
                selectedSources[i] = prior;
            }
            else
            {
                selectedSources[i] = new MetricReadingSource(candidate.ReadingId, candidate.Quantity,
                    definition.SourceRank, candidate.Availability, isPreferred, preferredDevice, preferredField);
                fieldsReused = false;
            }

            if (definition.LegacyAddress.Length == 0) continue;
            if (!legacyCandidates.TryGetValue(definition.LegacyAddress, out var choice))
            {
                legacyCandidates.Add(definition.LegacyAddress,
                    hasPreferred ? new(true, bestIndex) : new(false, -1));
            }
            else if (hasPreferred && (!choice.HasPreferred
                || IsPreferredLegacyCandidate(bestIndex, choice.CandidateIndex)))
            {
                legacyCandidates[definition.LegacyAddress] = new(true, bestIndex);
            }
        }

        legacyScratch.Clear();
        legacyScratch.EnsureCapacity(catalog.LegacyMetricHandles.Count);
        foreach (var (address, handle) in catalog.LegacyMetricHandles)
            legacyScratch.Add(address, handle);
        foreach (var (address, choice) in legacyCandidates)
        {
            if (!choice.HasPreferred) legacyScratch.Remove(address);
            else legacyScratch[address] = selectionDefinitions[choice.CandidateIndex].Binding.MetricHandle;
        }
        var legacyReused = known?.LegacyHandles is Dictionary<string, ulong> knownLegacy
            && SameLegacy(knownLegacy, legacyScratch);
        try
        {
            if (fieldsReused && legacyReused) return known!;
            IReadOnlyDictionary<ulong, MetricReadingSource> fields;
            if (fieldsReused)
            {
                fields = known!.Fields;
            }
            else
            {
                var created = new Dictionary<ulong, MetricReadingSource>(selectionDefinitions.Length);
                for (var i = 0; i < selectionDefinitions.Length; i++)
                    created.Add(selectionDefinitions[i].Binding.MetricHandle, selectedSources[i]!);
                fields = created;
            }
            var legacy = legacyReused ? known!.LegacyHandles
                : new Dictionary<string, ulong>(legacyScratch, StringComparer.OrdinalIgnoreCase);
            lastSelection = new(fields, legacy);
            lastSelectionDefinitions = selectionDefinitions;
            return lastSelection;
        }
        finally
        {
            Array.Clear(selectedSources);
        }
    }

    private static bool IsSource(MetricReadingSource source, string id, string quantity, int sourceRank,
        string availability, bool isPreferred, string? preferredDevice, string? preferredField)
        => string.Equals(source.Id, id, StringComparison.Ordinal)
            && string.Equals(source.Quantity, quantity, StringComparison.Ordinal)
            && source.SourceRank == sourceRank
            && string.Equals(source.SourceAvailability, availability, StringComparison.Ordinal)
            && source.IsPreferredSource == isPreferred
            && string.Equals(source.PreferredDeviceIdentity, preferredDevice, StringComparison.Ordinal)
            && string.Equals(source.PreferredFieldName, preferredField, StringComparison.Ordinal);

    // Same pairs in the same order: the published dictionary's enumeration
    // order is part of what a reused selection must reproduce.
    private static bool SameLegacy(Dictionary<string, ulong> known, Dictionary<string, ulong> current)
    {
        if (known.Count != current.Count) return false;
        var left = known.GetEnumerator();
        var right = current.GetEnumerator();
        while (left.MoveNext())
        {
            if (!right.MoveNext()) return false;
            if (!string.Equals(left.Current.Key, right.Current.Key, StringComparison.Ordinal)
                || left.Current.Value != right.Current.Value) return false;
        }
        return !right.MoveNext();
    }

    public HardwareMetricSnapshot ApplyProjectedFields(HardwareMetricSnapshot snapshot)
    {
        var source = snapshot.FieldBindings;
        // The published table is case-insensitive. Copy only to change a binding
        // or the comparer; otherwise the incoming immutable table is published.
        var bindings = IsOrdinalIgnoreCase(source) ? null
            : new Dictionary<string, MetricDeviceFieldBinding>(source, StringComparer.OrdinalIgnoreCase);
        RemoveStaleProjections(bindings ?? source);
        foreach (var (address, binding) in source)
        {
            if (binding.ControlTarget is not null || (binding.Field.Reading is not null
                && binding.Field.Code != MetricFieldCode.NpuDeviceEngineBusyPercent)) continue;
            var epoch = sourceCapabilityEpochs.GetValueOrDefault(binding.SourceId);
            if (projectedCapabilities.GetValueOrDefault(address)?.Epoch != epoch) projectedCapabilities.Remove(address);
            var status = snapshot.Datasets.GetValueOrDefault(address)?.Status;
            if (status == SamplingObservationStatus.Current) SetProjectedCapability(address, epoch, "available");
            else if (status == SamplingObservationStatus.Unsupported || binding.Field.Reading?.SourceAvailability == "unsupported")
                SetProjectedCapability(address, epoch, "unsupported");
            var availability = projectedCapabilities.GetValueOrDefault(address)?.Availability ?? "unverified";
            var rule = MetricFieldRegistry.ReadingRule(binding.Field, binding.SourceId, nvmeDevice: false);
            var id = GetReadingId(binding.DeviceIdentity, rule.Quantity);
            var isPreferred = availability == "available";
            var preferredDevice = isPreferred ? binding.DeviceIdentity : null;
            var preferredField = isPreferred ? binding.Field.Name : null;
            var projected = projectedBindings.TryGetValue(address, out var prior)
                && ReferenceEquals(prior.Source, binding)
                && prior.Projected.Field.Reading is { } priorReading
                && IsSource(priorReading, id, rule.Quantity, rule.SourceRank, availability, isPreferred,
                    preferredDevice, preferredField)
                    ? prior.Projected
                    : binding with { Field = binding.Field with { Reading = new(id, rule.Quantity,
                        rule.SourceRank, availability, isPreferred, preferredDevice, preferredField) } };
            projectedBindings[address] = (binding, projected);
            bindings ??= new Dictionary<string, MetricDeviceFieldBinding>(source, StringComparer.OrdinalIgnoreCase);
            bindings[address] = projected;
        }
        return bindings is null ? snapshot : snapshot with { FieldBindings = bindings };
    }

    private void SetProjectedCapability(string address, ulong epoch, string availability)
    {
        if (projectedCapabilities.TryGetValue(address, out var current)
            && current.Epoch == epoch && current.Availability == availability) return;
        projectedCapabilities[address] = new(epoch, availability);
    }

    private static bool IsOrdinalIgnoreCase(IReadOnlyDictionary<string, MetricDeviceFieldBinding> bindings)
        => bindings switch
        {
            Dictionary<string, MetricDeviceFieldBinding> dictionary =>
                ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, MetricDeviceFieldBinding> immutable =>
                ReferenceEquals(immutable.KeyComparer, StringComparer.OrdinalIgnoreCase),
            _ => false
        };

    private void RemoveStaleProjections(IReadOnlyDictionary<string, MetricDeviceFieldBinding> bindings)
    {
        List<string>? stale = null;
        foreach (var key in projectedCapabilities.Keys)
            if (!bindings.ContainsKey(key)) (stale ??= []).Add(key);
        foreach (var key in projectedBindings.Keys)
            if (!bindings.ContainsKey(key)) (stale ??= []).Add(key);
        if (stale is null) return;
        foreach (var key in stale)
        {
            projectedCapabilities.Remove(key);
            projectedBindings.Remove(key);
        }
    }

    public MetricSampleRequest ExpandLegacyRequest(MetricSampleRequest request,
        NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity)
        => ExpandLegacyRequest(request, frame.Catalog, frame.Header, frame.Sources, workspaceIdentity);

    internal MetricSampleRequest ExpandLegacyRequest(MetricSampleRequest request,
        NativeMetricSnapshotCatalogProjection catalog,
        NativeMetricSnapshotSnapshotHeader header,
        IReadOnlyList<NativeMetricSnapshotSourceOutput> sources,
        ulong workspaceIdentity)
    {
        if (request.IsAll || request.IsCatalogProbe) return request;
        var selection = Select(catalog, header, sources, workspaceIdentity);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in request.Ids)
        {
            var bindings = catalog.MetricBindings.Values
                .Where(row => string.Equals(LegacyAddress(row), id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bindings.Length == 0) { expanded.Add(id); continue; }
            if (selection.LegacyHandles.TryGetValue(id, out var handle))
                expanded.Add(catalog.MetricIds[handle]);
            // Unverified support is probed through the same requested target
            // and owner; no new sampler or polling loop. Transient failures
            // remain subject to the existing source cooldown.
            var readingIds = bindings.Select(row => selection.Fields.GetValueOrDefault(row.MetricHandle)?.Id)
                .Where(static id => id is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var binding in catalog.MetricBindings.Values
                         .Where(row => readingIds.Contains(selection.Fields.GetValueOrDefault(row.MetricHandle)?.Id)))
                if (selection.Fields.GetValueOrDefault(binding.MetricHandle)?.SourceAvailability == "unverified")
                    expanded.Add(binding.MetricId);
        }
        var result = request.IncludesAllGpuCoreMetrics
            ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(expanded) : MetricSampleRequest.ForIds(expanded);
        return request.IncludesCatalog ? result.WithCatalog() : result;
    }

    private void EnsureGeneration(NativeMetricSnapshotSnapshotHeader header, ulong workspaceIdentity)
    {
        if (workspace == workspaceIdentity && configuration == header.ConfigurationGeneration
            && catalogGeneration == header.CatalogGeneration) return;
        capabilities.Clear(); quantities.Clear(); projectedCapabilities.Clear(); sourceCapabilityEpochs.Clear(); integratedGpu = null;
        workspace = workspaceIdentity;
        configuration = header.ConfigurationGeneration;
        catalogGeneration = header.CatalogGeneration;
        sourceRows.Clear();
        readingIds.Clear();
        sourceRows.TrimExcess();
        readingIds.TrimExcess();
        selectionDefinitions = [];
        selectionDefinitionsValid = false;
        preferredCandidates.Clear();
        legacyCandidates.Clear();
        preferredCandidates.TrimExcess();
        legacyCandidates.TrimExcess();
        lastSelection = null;
        lastSelectionDefinitions = null;
        selectedSources = [];
        legacyScratch.Clear();
        legacyScratch.TrimExcess();
        projectedBindings.Clear();
        projectedBindings.TrimExcess();
    }

    private void LoadSourceRows(IReadOnlyList<NativeMetricSnapshotSourceOutput> sources)
    {
        sourceRows.EnsureCapacity(sources.Count);
        sourceRows.Clear();
        foreach (var source in sources) sourceRows.Add(source.SourceHandle, source);
    }

    private void EnsureSelectionDefinitions(NativeMetricSnapshotCatalogProjection catalog)
    {
        if (selectionDefinitionsValid) return;
        var nvmeDevices = new HashSet<string>(StringComparer.Ordinal);
        var definitionCount = 0;
        foreach (var binding in catalog.MetricBindings.Values)
        {
            if (binding.Field is null) continue;
            definitionCount++;
            if (binding.FieldCode == MetricFieldCode.NvmeCompositeTemperature)
                nvmeDevices.Add(binding.DeviceIdentity);
        }

        var definitions = new SelectionDefinition[definitionCount];
        // Only this catalog's physical readings share an ID. The temporary index
        // is discarded after construction; definitions own the resulting strings.
        var catalogReadingIds = new Dictionary<(string Device, string Quantity), string>();
        var candidateIndex = 0;
        foreach (var binding in catalog.MetricBindings.Values)
        {
            if (binding.Field is not { } field) continue;
            var unmappedApu = binding.FieldCode == MetricFieldCode.CpuIgpuTemperature && integratedGpu is null;
            var device = binding.FieldCode == MetricFieldCode.CpuIgpuTemperature
                && binding.SourceId == "amd.smu" && integratedGpu is not null
                ? integratedGpu : binding.DeviceIdentity;
            var rule = MetricFieldRegistry.ReadingRule(
                field,
                binding.SourceId,
                nvmeDevices.Contains(binding.DeviceIdentity));
            var quantity = unmappedApu ? "cpu.apu_temperature_unmapped" : rule.Quantity;
            var readingKey = (device, quantity);
            if (!catalogReadingIds.TryGetValue(readingKey, out var readingId))
            {
                readingId = ReadingId(device, quantity);
                catalogReadingIds.Add(readingKey, readingId);
            }
            definitions[candidateIndex++] = new(
                binding,
                device,
                quantity,
                rule.SourceRank,
                rule.AliasRank,
                readingId,
                LegacyAddress(binding),
                unmappedApu,
                binding.FieldCode == MetricFieldCode.CpuTemperature
                    && binding.SourceId == "hardware-monitor.core",
                false);
        }

        var readingGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var definition in definitions)
            readingGroupCounts[definition.ReadingId] = readingGroupCounts.GetValueOrDefault(definition.ReadingId) + 1;
        var dynamicDevices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
            if (definition.DynamicQuantity) dynamicDevices.Add(definition.Device);
        var sharedCandidateCount = 0;
        var legacyAddressCount = 0;
        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            var sharedReadingId = readingGroupCounts[definition.ReadingId] != 1
                || dynamicDevices.Contains(definition.Device);
            if (sharedReadingId)
            {
                definitions[i] = definition with { SharedReadingId = true };
                sharedCandidateCount++;
            }
            if (definition.LegacyAddress.Length != 0) legacyAddressCount++;
        }

        selectionDefinitions = definitions;
        preferredCandidates.EnsureCapacity(sharedCandidateCount);
        legacyCandidates.EnsureCapacity(legacyAddressCount);
        selectionDefinitionsValid = true;
    }

    private void InvalidateSelectionDefinitions()
    {
        selectionDefinitions = [];
        selectionDefinitionsValid = false;
        readingIds.Clear();
        readingIds.TrimExcess();
        preferredCandidates.Clear();
        preferredCandidates.TrimExcess();
        legacyCandidates.Clear();
    }

    private string GetReadingId(string device, string quantity)
    {
        var key = (device, quantity);
        if (readingIds.TryGetValue(key, out var readingId))
            return readingId;
        readingId = ReadingId(device, quantity);
        readingIds.Add(key, readingId);
        return readingId;
    }

    private Candidate CreateCandidate(SelectionDefinition definition)
    {
        var binding = definition.Binding;
        var fact = capabilities.GetValueOrDefault(binding.MetricHandle);
        var currentCapability = sourceRows.TryGetValue(binding.SourceHandle, out var source)
            && fact?.Epoch == source.CapabilityGeneration;
        var quantity = definition.Quantity;
        if (!definition.UnmappedApu
            && currentCapability
            && binding.FieldCode == MetricFieldCode.CpuTemperature
            && binding.SourceId == "hardware-monitor.core"
            && quantities.TryGetValue(binding.MetricHandle, out var declaredQuantity))
            quantity = declaredQuantity;
        var readingId = string.Equals(quantity, definition.Quantity, StringComparison.Ordinal)
            ? definition.ReadingId
            : GetReadingId(definition.Device, quantity);
        return new(quantity, currentCapability ? fact!.Availability : "unverified", readingId);
    }

    private bool IsPreferredCandidate(int candidateIndex, int currentIndex)
    {
        var candidate = selectionDefinitions[candidateIndex];
        var current = selectionDefinitions[currentIndex];
        var comparison = candidate.SourceRank.CompareTo(current.SourceRank);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.AliasRank.CompareTo(current.AliasRank);
        if (comparison != 0) return comparison < 0;
        comparison = StringComparer.Ordinal.Compare(candidate.Binding.Field!.Name, current.Binding.Field!.Name);
        return comparison != 0
            ? comparison < 0
            : StringComparer.Ordinal.Compare(candidate.Binding.DeviceIdentity, current.Binding.DeviceIdentity) < 0;
    }

    private bool IsPreferredLegacyCandidate(int candidateIndex, int currentIndex)
    {
        var candidate = selectionDefinitions[candidateIndex].Binding;
        var current = selectionDefinitions[currentIndex].Binding;
        var comparison = selectionDefinitions[candidateIndex].SourceRank
            .CompareTo(selectionDefinitions[currentIndex].SourceRank);
        if (comparison != 0) return comparison < 0;
        comparison = sourceRows[candidate.SourceHandle].Priority.CompareTo(
            sourceRows[current.SourceHandle].Priority);
        return comparison != 0
            ? comparison < 0
            : candidate.MetricHandle < current.MetricHandle;
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
