namespace ResourceManager.App.Domain.Settings;

// One WER LocalDumps key as observed in the registry, independent of the requested preference.
// Owned means the key carries Resource Manager's owner marker, so off/ask may remove it.
public sealed record CrashDumpRegistryEntry(
    string Image,
    bool Present,
    bool OwnedByResourceManager,
    string? DumpFolder = null,
    int? DumpType = null,
    int? CustomDumpFlags = null,
    int? DumpCount = null);

// 崩溃转储 desired-vs-actual state, like ServiceRecoveryDiagnostics. EffectiveMode is the stored mode with
// "default" resolved by the release channel; ModeSource says which of the two decided it.
public sealed record CrashDumpDiagnostics(
    string State,
    string EffectiveMode,
    string ModeSource,
    string DefaultMode,
    string Channel,
    string DumpFolder,
    IReadOnlyList<string> Images,
    int KeepCountMaximum,
    IReadOnlyList<CrashDumpRegistryEntry>? Actual = null,
    string? SkipReason = null,
    string? WriteError = null,
    string? ReadError = null);

public static class CrashDumpModeSources
{
    public const string User = "user";
    public const string ChannelDefault = "channelDefault";
}

public static class ReleaseChannels
{
    public const string Beta = "beta";
    public const string Stable = "stable";
}
