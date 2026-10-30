namespace ResourceManager.App.Domain.Settings;

/// <summary>User-owned targeted recording profiles. A settings file without this section reads as the
/// single 默认 profile; the profile list is never empty once saved.</summary>
public sealed record AppTargetedRecordingSettings(IReadOnlyList<TargetedRecordingProfile> Profiles);

/// <summary>A named list of recording items. DefaultIntervalMs applies to items whose IntervalMs is null.</summary>
public sealed record TargetedRecordingProfile(
    string Id,
    string Name,
    int DefaultIntervalMs,
    IReadOnlyList<TargetedRecordingItem> Items);

/// <summary>One recorded item. Kind is frames, metric (a /api/metrics/catalog id) or software (a resource
/// breakdown metric or field for the target software). Source is auto or one concrete source of that item.
/// IntervalMs null means the item's natural interval, else the profile default. SyncPrecision is stored and
/// reported only; delivery does not depend on it yet.</summary>
public sealed record TargetedRecordingItem(
    string Kind,
    string Id,
    string Source = TargetedRecordingProfileDefaults.AutoSource,
    int? IntervalMs = null,
    string SyncPrecision = TargetedRecordingSyncPrecisions.Mid);

public static class TargetedRecordingItemKinds
{
    public const string Frames = "frames";
    public const string Metric = "metric";
    public const string Software = "software";
    public static readonly IReadOnlyList<string> All = [Frames, Metric, Software];
}

public static class TargetedRecordingSyncPrecisions
{
    public const string Low = "low";
    public const string Mid = "mid";
    public const string High = "high";
    public static readonly IReadOnlyList<string> All = [Low, Mid, High];
}

public static class TargetedRecordingProfileDefaults
{
    public const string DefaultProfileId = "default";
    public const string DefaultProfileName = "默认";
    public const string FramesItemId = "frames.present";
    public const string AutoSource = "auto";
    public const int DefaultIntervalMs = 1000;
    public const int MinimumIntervalMs = 100;
    public const int MaximumIntervalMs = 60_000;
    public const int MaximumNameLength = 64;
    public const int MaximumProfiles = 64;
    public const int MaximumItems = 256;

    /// <summary>The item set the recorder used before profiles existed: application frames, the target's
    /// resource breakdown shares and the system readings that were written into every sample.</summary>
    public static TargetedRecordingProfile CreateDefaultProfile() => new(
        DefaultProfileId,
        DefaultProfileName,
        DefaultIntervalMs,
        [
            new(TargetedRecordingItemKinds.Frames, FramesItemId),
            new(TargetedRecordingItemKinds.Software, "cpu.usage"),
            new(TargetedRecordingItemKinds.Software, "memory.usage"),
            new(TargetedRecordingItemKinds.Software, "virtualMemory.usage"),
            new(TargetedRecordingItemKinds.Software, "disk.read"),
            new(TargetedRecordingItemKinds.Software, "disk.write"),
            new(TargetedRecordingItemKinds.Software, "network.receive"),
            new(TargetedRecordingItemKinds.Software, "network.send"),
            new(TargetedRecordingItemKinds.Software, "gpu.0.usage"),
            new(TargetedRecordingItemKinds.Software, "gpu.0.vram"),
            new(TargetedRecordingItemKinds.Metric, "cpu.usage"),
            new(TargetedRecordingItemKinds.Metric, "cpu.temperature"),
            new(TargetedRecordingItemKinds.Metric, "cpu.frequency"),
            new(TargetedRecordingItemKinds.Metric, "memory.usage"),
            new(TargetedRecordingItemKinds.Metric, "gpu.0.usage"),
            new(TargetedRecordingItemKinds.Metric, "gpu.0.temperature"),
            new(TargetedRecordingItemKinds.Metric, "gpu.0.vram")
        ]);

    public static AppTargetedRecordingSettings Create() => new([CreateDefaultProfile()]);
}

/// <summary>Structural rules every saved profile list satisfies. Catalog membership (does this metric or
/// source exist on this machine) is checked by the profile endpoints, which own the live catalogs.</summary>
public static class TargetedRecordingProfileShape
{
    public static string? Validate(AppTargetedRecordingSettings? settings)
    {
        if (settings is null) return null;
        if (settings.Profiles is not { Count: > 0 } profiles) return "targeted-recording-profiles-empty";
        if (profiles.Count > TargetedRecordingProfileDefaults.MaximumProfiles) return "targeted-recording-profiles-too-many";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id)
                || profile.Id.Length > TargetedRecordingProfileDefaults.MaximumNameLength)
                return "targeted-recording-profile-invalid-id";
            if (!ids.Add(profile.Id)) return "targeted-recording-profile-duplicate-id";
            if (ValidateProfile(profile) is { } reason) return reason;
        }
        return null;
    }

    public static string? ValidateProfile(TargetedRecordingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > TargetedRecordingProfileDefaults.MaximumNameLength)
            return "targeted-recording-profile-invalid-name";
        if (!IsIntervalInRange(profile.DefaultIntervalMs)) return "targeted-recording-interval-out-of-range";
        if (profile.Items is not { Count: > 0 } items) return "targeted-recording-profile-items-empty";
        if (items.Count > TargetedRecordingProfileDefaults.MaximumItems) return "targeted-recording-profile-items-too-many";
        var keys = new HashSet<(string, string)>();
        foreach (var item in items)
        {
            if (ValidateItem(item) is { } reason) return reason;
            if (!keys.Add((item.Kind, item.Id.ToLowerInvariant()))) return "targeted-recording-item-duplicate";
        }
        return null;
    }

    public static string? ValidateItem(TargetedRecordingItem? item)
    {
        if (item is null || item.Kind is null || !TargetedRecordingItemKinds.All.Contains(item.Kind, StringComparer.Ordinal))
            return "targeted-recording-item-invalid-kind";
        if (string.IsNullOrWhiteSpace(item.Id)) return "targeted-recording-item-unknown";
        if (string.IsNullOrWhiteSpace(item.Source)) return "targeted-recording-item-source-invalid";
        if (item.SyncPrecision is null || !TargetedRecordingSyncPrecisions.All.Contains(item.SyncPrecision, StringComparer.Ordinal))
            return "targeted-recording-item-invalid-precision";
        if (item.Kind == TargetedRecordingItemKinds.Frames)
        {
            if (!string.Equals(item.Id, TargetedRecordingProfileDefaults.FramesItemId, StringComparison.Ordinal))
                return "targeted-recording-item-unknown";
            if (!string.Equals(item.Source, TargetedRecordingProfileDefaults.AutoSource, StringComparison.Ordinal))
                return "targeted-recording-item-source-invalid";
            // Frames are recorded per present; a sampling interval does not apply.
            return item.IntervalMs is null ? null : "targeted-recording-interval-out-of-range";
        }
        return item.IntervalMs is null || IsIntervalInRange(item.IntervalMs.Value)
            ? null : "targeted-recording-interval-out-of-range";
    }

    public static bool IsIntervalInRange(int intervalMs)
        => intervalMs >= TargetedRecordingProfileDefaults.MinimumIntervalMs
            && intervalMs <= TargetedRecordingProfileDefaults.MaximumIntervalMs;
}
