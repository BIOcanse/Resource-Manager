using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>POST /api/performance-overlay/toggle body: both fields optional.</summary>
public sealed record PerformanceOverlayToggleRequest(string? SoftwareId = null, bool? Enabled = null);

/// <summary>What the toggle wrote: the software it acted on (by the stored spelling of its id), its name
/// when the backend resolved it from the foreground, the new value and the settings revision.</summary>
public sealed record PerformanceOverlayToggleReceipt(string SoftwareId, string? SoftwareName, bool Enabled, string Revision)
{
    public string? RuntimeApplicationDisposition { get; init; }
    public string? RuntimeFailureCode { get; init; }
}

/// <summary>Why a toggle or a foreground read did not act (409 body).</summary>
public sealed record PerformanceOverlayRejection(string Reason);

/// <summary>GET /api/performance-overlay/software/{softwareId}: the stored entry, or the declared default
/// entry when none is stored, with the revision a PATCH of /api/settings/app names.</summary>
public sealed record PerformanceOverlaySoftwareView(string SoftwareId, bool Stored, string Revision,
    AppPerformanceOverlaySoftwareSettings Settings, PerformanceOverlayEditingConstraints EditingConstraints);

/// <summary>Read-only editor limits projected from the backend's declared overlay defaults; never persisted as settings.</summary>
public sealed record PerformanceOverlayEditingConstraints(int MaximumItems, int FontSizePxMinimum, int FontSizePxMaximum,
    int MarginPxMinimum, int MarginPxMaximum, double RegionRatioMinimum, double RegionRatioMaximum, double RegionRatioStep)
{
    public static PerformanceOverlayEditingConstraints FromDefaults(PerformanceOverlayDefaults defaults) => new(
        defaults.MaximumItems, defaults.FontSizePxMinimum, defaults.FontSizePxMaximum,
        defaults.MarginPxMinimum, defaults.MarginPxMaximum, defaults.RegionRatioMinimum, defaults.RegionRatioMaximum,
        defaults.RegionRatioStep);
}

/// <summary>GET /api/performance-overlay/foreground: the attributable software of the foreground (or of the
/// named process) and its current overlay switch.</summary>
public sealed record PerformanceOverlayForegroundView(string SoftwareId, string SoftwareName, bool Enabled);

/// <summary>The software a foreground or named process belongs to, or why there is none.</summary>
public sealed record ForegroundSoftwareResolution(string? SoftwareId, string? SoftwareName, string? Reason)
{
    public static ForegroundSoftwareResolution Found(string id, string name) => new(id, name, null);
    public static ForegroundSoftwareResolution Rejected(string reason) => new(null, null, reason);
}

public static class PerformanceOverlayRejections
{
    /// <summary>No selected desktop publisher has an observation for the active console session.</summary>
    public const string NoDesktopObservation = "no-desktop-observation";
    /// <summary>The observation does not know a foreground window, or there is none.</summary>
    public const string NoForegroundWindow = "no-foreground-window";
    /// <summary>The process inventory with attribution could not be read.</summary>
    public const string ProcessInventoryUnavailable = "process-inventory-unavailable";
    /// <summary>The foreground process is not in the current inventory (it exited, or the PID was reused).</summary>
    public const string ForegroundProcessNotFound = "foreground-process-not-found";
    /// <summary>The foreground process belongs to no software.</summary>
    public const string ForegroundNotAttributed = "foreground-not-attributed";
}

/// <summary>
/// The toggle: settings are the only state. It reads the current settings once, flips (or sets) the
/// software's <c>enabled</c>, and writes through the same settings patch path every other writer uses, so
/// subscribers (the resident's overlay block, open pages) see the change through /api/settings/app/subscribe.
/// A software without an entry gets one with only <c>enabled</c>; the normalizer fills the declared defaults.
/// </summary>
public static class PerformanceOverlayToggle
{
    /// <summary>Inspect the backend publication only. Identity and attribution are separate datasets;
    /// an inventory row alone is not evidence of software ownership.</summary>
    internal static ForegroundSoftwareResolution ResolvePublishedSoftware(
        ISchedulingProcessFactObservationSource source, int processId, long? processStartKey)
    {
        var current = source.ReadLatest(new(SchedulingProcessMetricMask.None,
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
        if (current?.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _) != true
            || !current.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessAttribution, out _))
            return ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.ProcessInventoryUnavailable);
        var process = current.InventoryProcesses.FirstOrDefault(item => item.ProcessId == processId
            && (processStartKey is null || item.ProcessStartKey == (ulong)processStartKey.Value));
        if (process is null) return ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.ForegroundProcessNotFound);
        var attributed = current.AttributionProcesses.FirstOrDefault(item => item.ProcessId == process.ProcessId
            && item.ProcessStartKey == process.ProcessStartKey);
        if (attributed is null || string.IsNullOrWhiteSpace(attributed.SoftwareId) || attributed.SoftwareKind == SoftwareKinds.Unattributed)
            return ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.ForegroundNotAttributed);
        return ForegroundSoftwareResolution.Found(attributed.SoftwareId,
            string.IsNullOrWhiteSpace(attributed.SoftwareName) ? attributed.SoftwareId : attributed.SoftwareName);
    }

    public static async Task<(PerformanceOverlayToggleReceipt? Receipt, string? Rejection)> ToggleAsync(
        PerformanceOverlayToggleRequest? request,
        Func<CancellationToken, Task<ForegroundSoftwareResolution>> resolveForeground,
        Func<CancellationToken, Task<AppSettingsUpdateResult>> load,
        Func<AppSettingsPatchRequest, CancellationToken, Task<AppSettingsUpdateResult>> patch,
        CancellationToken cancellationToken)
    {
        if (request?.SoftwareId is { } requestedId && string.IsNullOrWhiteSpace(requestedId))
            return (null, "performance-overlay-invalid-software-id");
        string softwareId;
        string? softwareName = null;
        if (!string.IsNullOrWhiteSpace(request?.SoftwareId)) softwareId = request.SoftwareId.Trim();
        else
        {
            var foreground = await resolveForeground(cancellationToken);
            if (foreground.SoftwareId is null) return (null, foreground.Reason ?? PerformanceOverlayRejections.ForegroundNotAttributed);
            softwareId = foreground.SoftwareId;
            softwareName = foreground.SoftwareName;
        }
        var current = await load(cancellationToken);
        var stored = Find(current.Settings.PerformanceOverlay, softwareId);
        var key = stored?.Key ?? softwareId;
        var enabled = request?.Enabled ?? !(stored?.Value.Enabled ?? false);
        var entry = stored is { } found
            ? JsonSerializer.SerializeToNode(found.Value with { Enabled = enabled }, EntryJson)!
            : new JsonObject { ["enabled"] = enabled };
        var changes = new JsonObject { ["performanceOverlay"] = new JsonObject { ["software"] = new JsonObject { [key] = entry } } };
        using var document = JsonDocument.Parse(changes.ToJsonString());
        var result = await patch(new AppSettingsPatchRequest(current.Revision, document.RootElement),
            cancellationToken);
        var storedResult = Find(result.Settings.PerformanceOverlay, key);
        var savedNotApplied = result.RuntimeApplicationDisposition == "savedNotApplied"
            && storedResult?.Value.Enabled == enabled;
        if (!savedNotApplied && result.RuntimeApplicationDisposition is not (AppSettingsRuntimeApplicationDisposition.CommittedAndApplied
            or AppSettingsRuntimeApplicationDisposition.CommittedWithCapabilityConstraints
            or AppSettingsRuntimeApplicationDisposition.CommittedWithDeliveryFailures))
            return (null, result.RuntimeFailureCode ?? result.RuntimeApplicationDisposition);
        return (new PerformanceOverlayToggleReceipt(key, softwareName, enabled, result.Revision)
        {
            RuntimeApplicationDisposition = result.RuntimeApplicationDisposition,
            RuntimeFailureCode = result.RuntimeFailureCode
        }, null);
    }

    /// <summary>The stored entry whose id equals <paramref name="softwareId"/> ignoring case.</summary>
    public static KeyValuePair<string, AppPerformanceOverlaySoftwareSettings>? Find(AppPerformanceOverlaySettings? settings, string softwareId)
    {
        foreach (var pair in settings?.Software ?? new Dictionary<string, AppPerformanceOverlaySoftwareSettings>())
            if (string.Equals(pair.Key, softwareId, StringComparison.OrdinalIgnoreCase)) return pair;
        return null;
    }

    private static readonly JsonSerializerOptions EntryJson = new AppSettingsJsonContext(new(JsonSerializerDefaults.Web)).Options;
}
