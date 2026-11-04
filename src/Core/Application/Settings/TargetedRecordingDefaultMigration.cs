using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

/// <summary>COMPAT (delete when every supported upgrade starts from a release that carries this migration, i.e.
/// no supported installation can still hold a settings file written before 2026-10-31): the 默认 profile before
/// 2026-10-31 had 17 items with the GPU items fixed to gpu.0.*, and some installations saved it into their
/// settings file. A stored profile equal to that old default item by item (same id, name, default interval, and
/// the same items in the same order with auto source, no own interval and mid precision) is replaced by the
/// current 默认 profile when the settings are read; any profile that differs in anything is kept as stored.
/// The list below is the historical record of that old default, not a choice.</summary>
internal static class TargetedRecordingDefaultMigration
{
    private const string OldName = "默认";
    private const int OldDefaultIntervalMs = 1000;

    private static readonly (string Kind, string Id)[] OldItems =
    [
        (TargetedRecordingItemKinds.Frames, "frames.present"),
        (TargetedRecordingItemKinds.Software, "cpu.usage"),
        (TargetedRecordingItemKinds.Software, "memory.usage"),
        (TargetedRecordingItemKinds.Software, "virtualMemory.usage"),
        (TargetedRecordingItemKinds.Software, "disk.read"),
        (TargetedRecordingItemKinds.Software, "disk.write"),
        (TargetedRecordingItemKinds.Software, "network.receive"),
        (TargetedRecordingItemKinds.Software, "network.send"),
        (TargetedRecordingItemKinds.Software, "gpu.0.usage"),
        (TargetedRecordingItemKinds.Software, "gpu.0.vram"),
        (TargetedRecordingItemKinds.Metric, "cpu.usage"),
        (TargetedRecordingItemKinds.Metric, "cpu.temperature"),
        (TargetedRecordingItemKinds.Metric, "cpu.frequency"),
        (TargetedRecordingItemKinds.Metric, "memory.usage"),
        (TargetedRecordingItemKinds.Metric, "gpu.0.usage"),
        (TargetedRecordingItemKinds.Metric, "gpu.0.temperature"),
        (TargetedRecordingItemKinds.Metric, "gpu.0.vram")
    ];

    /// <summary>The settings with an exact old default profile replaced by the current default; the same instance
    /// when there is none.</summary>
    public static AppTargetedRecordingSettings Apply(AppTargetedRecordingSettings settings)
    {
        if (settings.Profiles is not { Count: > 0 } profiles || !profiles.Any(IsOldDefault)) return settings;
        return settings with
        {
            Profiles = profiles.Select(static profile => IsOldDefault(profile)
                ? TargetedRecordingProfileDefaults.CreateDefaultProfile() : profile).ToArray()
        };
    }

    public static bool IsOldDefault(TargetedRecordingProfile? profile)
    {
        if (profile is null || profile.Id != TargetedRecordingProfileDefaults.DefaultProfileId || profile.Name != OldName
            || profile.DefaultIntervalMs != OldDefaultIntervalMs || profile.Items is not { } items || items.Count != OldItems.Length)
            return false;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item is null || item.Kind != OldItems[index].Kind || item.Id != OldItems[index].Id
                || (item.Source ?? TargetedRecordingProfileDefaults.AutoSource) != TargetedRecordingProfileDefaults.AutoSource
                || item.IntervalMs is not null
                || (item.SyncPrecision ?? TargetedRecordingSyncPrecisions.Mid) != TargetedRecordingSyncPrecisions.Mid)
                return false;
        }
        return true;
    }
}
