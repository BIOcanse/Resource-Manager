using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMetricFieldEndpoints();
        app.MapGet("/api/metrics/catalog", async (
            HardwareMetricCatalogState catalogState,
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
        {
            // Keep unavailable definitions so consumers can display the missing capability.
            if (catalogState.Current is not { } snapshot)
                return Results.Ok(Array.Empty<MetricDefinition>());
            return Results.Ok(MetricCatalog.WithInstalledComponents(
                MetricCatalog.FromSnapshot(snapshot),
                await InstalledComponentIdsAsync(componentManager, cancellationToken)));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSampler sampler,
            HardwareMetricCatalogState catalogState,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var snapshotRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : MetricSampleRequest.ForIds([]);
            var snapshot = await sampler.GetSnapshotAsync(snapshotRequest, cancellationToken);
            return Results.Ok(MetricSnapshotWireSnapshot.From(snapshot, snapshotRequest, catalogState.Revision));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/gpu-specialized", async (
            HttpRequest request,
            HttpResponse response,
            IGpuTelemetryPushSource gpuTelemetry,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            // No ids means no counter demand, matching the main metrics snapshot.
            var counterIds = request.Query.TryGetValue("ids", out var ids)
                ? ids.Select(static id => id ?? string.Empty) : [];
            var sampleRequest = GpuTelemetryWorkerRequest.Create(counterIds,
                GpuTelemetryWorkerDetailLevel.AdvancedCounters, timeoutMilliseconds: 500);
            var snapshot = await gpuTelemetry.FetchOnceAsync("gpu-specialized:snapshot", sampleRequest, cancellationToken);
            return Results.Ok(snapshot is null ? null : GpuTelemetryWireSnapshot.From(snapshot));
        }).AllowAnonymous();
        return app;
    }

    // Hardware providers are machine-wide, so the caller's own install roots do not matter here.
    private static async Task<IReadOnlySet<string>> InstalledComponentIdsAsync(
        IComponentManager componentManager,
        CancellationToken cancellationToken)
        => (await componentManager.GetStatusesAsync(cancellationToken))
            .Where(static status => status.Installed)
            .Select(static status => status.Definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEndpointRouteBuilder MapMetricFieldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics/fields/capabilities", ([Microsoft.AspNetCore.Mvc.FromServices] ResourceManager.ReadWrite.ReadWriteCore core)
            => Results.Ok(PublicCapabilityFields(core.Catalog.Fields))).AllowAnonymous();

        app.MapGet("/api/metrics/fields/catalog", (HardwareMetricCatalogState catalogState, HttpContext context)
            => catalogState.Current is { } snapshot
                ? Results.Ok(ReadMetricFieldCatalog(snapshot, context.RequestServices))
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();

        app.MapGet("/api/metrics/fields/snapshot", async (
            HttpRequest request, HttpResponse response, HardwareMetricCatalogState catalogState,
            IMetricSampler sampler, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            IReadOnlyList<MetricFieldSelection> selections;
            try { selections = ParseMetricFieldSelections(request.Query); }
            catch (ArgumentException) { return Results.BadRequest(); }
            if (catalogState.Current is not { } catalog) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            MetricSampleRequest selection;
            try { selection = MetricSampleRequest.Merge(selections.Select(item =>
                MetricDeviceFields.CreateRequest(catalog, item.Device, item.Fields))); }
            catch (ArgumentException) { return Results.BadRequest(); }
            var snapshot = await sampler.GetSnapshotAsync(selection, cancellationToken);
            return Results.Ok(ReadMetricFields(snapshot, selections, request.HttpContext.RequestServices));
        }).AllowAnonymous();

        return app;
    }

    internal static IReadOnlyList<FieldCapability> PublicCapabilityFields(
        IEnumerable<FieldCapability> fields)
        => fields.Where(static field => !string.Equals(
            field.ReadSource,
            DeviceInformationFieldRegistry.MftSourceId,
            StringComparison.Ordinal)).ToArray();

    private static TimeSpan ParseSamplingSubscriptionInterval(
        IQueryCollection query)
    {
        const int defaultIntervalMilliseconds = 1_000;
        if (!query.TryGetValue("intervalMs", out var raw)
            || !int.TryParse(raw.FirstOrDefault(), out var milliseconds))
        {
            milliseconds = defaultIntervalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 60_000));
    }

    private static string? ParseMetricFieldDevice(IQueryCollection query)
    {
        if (!query.TryGetValue("device", out var values)) return null;
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new ArgumentException("metric-field-device-invalid", nameof(query));
        return values[0];
    }

    private static IReadOnlySet<string> ParseMetricFieldNames(IQueryCollection query)
        => query["fields"].SelectMany(static value => (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

    private sealed record MetricFieldSelection(string? Device, IReadOnlySet<string> Fields);

    private static IReadOnlyList<MetricFieldSelection> ParseMetricFieldSelections(IQueryCollection query)
    {
        var devices = query["device"];
        if (devices.Count <= 1)
            return [new(ParseMetricFieldDevice(query), ParseMetricFieldNames(query)
                .Select(MetricFieldRegistry.NormalizePublicName).ToHashSet(StringComparer.Ordinal))];
        var groups = query["fields"];
        if (groups.Count != devices.Count || devices.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("metric-field-device-invalid", nameof(query));
        return Enumerable.Range(0, devices.Count).Select(index => new MetricFieldSelection(devices[index],
            (groups[index] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(MetricFieldRegistry.NormalizePublicName).ToHashSet(StringComparer.Ordinal))).ToArray();
    }

    private static MetricSampleRequest SelectMetricFields(HardwareMetricSnapshot catalog,
        IReadOnlyList<MetricFieldSelection> selections)
        => MetricSampleRequest.Merge(selections.Select(item => MetricDeviceFields.SelectBound(catalog, item.Device, item.Fields)));
}
