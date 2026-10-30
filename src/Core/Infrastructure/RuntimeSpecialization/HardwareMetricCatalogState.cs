using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Publications;
using System.Security.Cryptography;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HardwareMetricCatalogState
{
    private readonly object gate = new();
    private readonly CurrentValuePublicationSignal publications = new();
    private HardwareMetricCatalog? current;
    private readonly SemaphoreSlim compositionGate = new(1, 1);
    private IReadOnlySet<string> installedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DeviceTopologyPort>? ports;
    private MetricCatalogPublication? publication;
    private byte[] contentFingerprint = [];
    private string revision = Guid.NewGuid().ToString("N");

    public event Action? Changed;

    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(Changed))]
    public HardwareMetricSnapshot? Current => Volatile.Read(ref current)?.CreateSnapshot();

    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(Changed))]
    public HardwareMetricCatalog? Catalog => Volatile.Read(ref current);

    public string Revision => Volatile.Read(ref revision);

    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(Changed))]
    public (HardwareMetricSnapshot? Snapshot, string Revision) ReadCommitted()
    {
        lock (gate)
        {
            return (current?.CreateSnapshot(), revision);
        }
    }

    public MetricSampleRequest SelectSubscribable(MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsCatalogProbe || request.IsEmpty)
        {
            return request;
        }

        var snapshot = Current;
        if (snapshot is null)
        {
            return request;
        }

        var available = MetricCatalog.FromSnapshot(snapshot)
            .Where(static definition => definition.Selectable)
            .Select(static definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A bound field is subscribable even while its source has no current value.
        available.UnionWith(snapshot.FieldBindings.Keys);
        available.UnionWith(snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases));
        available.Add(MetricDatasetIds.SystemGpuInventory);
        var selected = request.IsAll
            ? available
            : request.Ids
                .Where(available.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesAllGpuCoreMetrics)
        {
            selected.UnionWith(available.Where(id => snapshot.FieldBindings.TryGetValue(id, out var binding)
                ? binding.Field.Code is MetricFieldCode.GpuUsage or MetricFieldCode.GpuMemoryUsed or MetricFieldCode.GpuMemoryPercent or MetricFieldCode.GpuEngineBusy
                : IsGpuCoreMetric(id)));
        }
        var selection = MetricSampleRequest.ForIds(selected);
        return request.IncludesCatalog ? selection.WithCatalog() : selection;
    }

    public void PublishCatalogProbe(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Action? changed = null;
        lock (gate)
        {
            Volatile.Write(ref current, HardwareMetricCatalog.FromSnapshot(snapshot));
            if (CommitContent()) changed = Changed;
        }

        changed?.Invoke();
    }

    public bool PublishReadingMetadata(HardwareMetricSnapshot snapshot)
    {
        Action? changed;
        lock (gate)
        {
            if (current is null) return false;
            var next = MetricReadingCatalog.Update(current, snapshot);
            if (ReferenceEquals(next, current)) return false;
            Volatile.Write(ref current, next);
            changed = CommitContent() ? Changed : null;
        }
        changed?.Invoke();
        return true;
    }

    private static bool IsGpuCoreMetric(string metricId)
    {
        if (!metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var separator = metricId.IndexOf('.', "gpu.".Length);
        if (separator <= "gpu.".Length)
        {
            return false;
        }
        var name = metricId[(separator + 1)..];
        return name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("engineBusy", StringComparison.OrdinalIgnoreCase);
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(Changed))]
    public MetricCatalogPublication? ReadPublication()
    {
        lock (gate) return publication;
    }

    public async IAsyncEnumerable<MetricCatalogPublication> SubscribeChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var changed = publications.Subscribe();
        var previous = ReadPublication();
        if (previous is not null) yield return previous;
        while (!cancellationToken.IsCancellationRequested)
        {
            await changed.WaitAsync(cancellationToken);
            var next = ReadPublication();
            // A notification may have been queued before the initial/current read.
            // Deliver each committed value once, even across that registration race.
            if (next is null || ReferenceEquals(previous, next)) continue;
            previous = next;
            yield return next;
        }
    }
    // Discovery owns refreshing these external facts. Transport only reads the commit.
    // Component files changed outside RM publish no revision; the next subscription start or GET reads them.
    [RuntimeRead(ReadVolatilityKind.EventDriven, "Catalog subscription started, catalog GET, or component revision published")]
    public async Task RefreshCompositionAsync(IComponentManager components,
        IDeviceTopologySnapshotProvider? topology, CancellationToken token)
    {
        await compositionGate.WaitAsync(token);
        try
        {
            var installed = (await components.GetStatusesAsync(token)).Where(static status => status.Installed)
                .Select(static status => status.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            CommitComposition(installed, topology);
        }
        finally { compositionGate.Release(); }
    }

    // Recommits the current snapshot and topology ports with the installed set already held.
    public async Task RecommitCompositionAsync(IDeviceTopologySnapshotProvider? topology, CancellationToken token)
    {
        await compositionGate.WaitAsync(token);
        try { CommitComposition(installed: null, topology); }
        finally { compositionGate.Release(); }
    }

    private void CommitComposition(IReadOnlySet<string>? installed, IDeviceTopologySnapshotProvider? topology)
    {
        var topologyPorts = topology?.ReadState().Snapshot?.Ports;
        Action? changed;
        lock (gate)
        {
            if (installed is not null) installedComponents = installed;
            ports = topologyPorts;
            changed = CommitContent() ? Changed : null;
        }
        changed?.Invoke();
    }

    private bool CommitContent()
    {
        if (current is null) return false;
        var snapshot = current.CreateSnapshot();
        var project = MetricDeviceProjection.Create(ports, snapshot.FieldBindings.Values);
        var next = new CatalogContent(MetricDeviceFields.Catalog(snapshot)
            .Select(value => value with { Device = project(value.Device) }).ToArray(),
            MetricCatalog.WithInstalledComponents(MetricCatalog.FromSnapshot(snapshot), installedComponents)
                .OrderBy(static metric => metric.Id, StringComparer.Ordinal).Select(static metric => metric with
                { CandidateSources = metric.CandidateSources.OrderBy(static source => source.DeviceIdentity, StringComparer.Ordinal)
                    .ThenBy(static source => source.FieldName, StringComparer.Ordinal).ThenBy(static source => source.SourceId, StringComparer.Ordinal).ToArray() }).ToArray());
        // Fingerprint the entire public value, including the composed facts, rather than discovery counters.
        // Matching digests still require exact content equality. Only the typed commit and
        // small digest stay resident; comparison buffers belong to this call.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, CatalogJsonContext.Default.CatalogContent);
        var fingerprint = SHA256.HashData(bytes);
        if (contentFingerprint.AsSpan().SequenceEqual(fingerprint) && publication is not null
            && bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(
                new CatalogContent(publication.Fields, publication.Metrics), CatalogJsonContext.Default.CatalogContent)))
            return false;
        contentFingerprint = fingerprint;
        Volatile.Write(ref revision, Guid.NewGuid().ToString("N"));
        publication = new(revision, next.Fields, next.Metrics);
        publications.Publish();
        return true;
    }

    private sealed record CatalogContent(IReadOnlyList<MetricDeviceFieldCatalog> Fields,
        IReadOnlyList<MetricDefinition> Metrics);

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(CatalogContent))]
    private partial class CatalogJsonContext : JsonSerializerContext;
}

public sealed record MetricCatalogPublication(string Revision, IReadOnlyList<MetricDeviceFieldCatalog> Fields,
    IReadOnlyList<MetricDefinition> Metrics);