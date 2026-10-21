using System.Collections.Immutable;
using System.Text;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

/// <summary>Explicit operation budgets, not NVAPI ABI limits. MaximumPayloadBytes charges
/// native profile/setting payload sizes; it is not a measurement of managed object overhead.</summary>
public sealed record NvidiaDrsReadLimits(uint MaximumProfileCount, uint MaximumSettingsPerProfile,
    uint MaximumTotalSettingCount, uint PageSize, ulong MaximumPayloadBytes);

public sealed record NvidiaDrsProfileRequest(string ProfileName, IReadOnlyList<uint> SettingIds);

/// <summary>Driver database declarations, not GPU state or settings proven active in an application.</summary>
public sealed record NvidiaDrsSnapshot(ImmutableArray<NvidiaDrsProfile> Profiles);

public sealed record NvidiaDrsProfile(string ProfileName, uint? GpuSupportRaw, uint? IsPredefinedRaw,
    uint? ApplicationCountReported, uint? SettingCountReported, ImmutableArray<NvidiaDrsSetting> Settings)
{
    // Strict UTF-16 preserves API lookup names without folding/trim or replacement collisions.
    public string DeviceIdentity => "nvidia-drs|profile|" +
        Convert.ToHexString(new UnicodeEncoding(false, false, true).GetBytes(ProfileName));
}

public sealed record NvidiaDrsSetting(uint SettingId, string? NameDeclared, uint? TypeRaw,
    uint? LocationRaw, uint? IsCurrentPredefinedRaw, uint? IsPredefinedValidRaw,
    NvidiaDrsCurrentValue? CurrentValue)
{
    public string IdentityOf(NvidiaDrsProfile profile) => profile.DeviceIdentity + "|setting|" + SettingId.ToString("X8");
}

public abstract record NvidiaDrsCurrentValue;
public sealed record NvidiaDrsDwordValue(uint Value) : NvidiaDrsCurrentValue;
/// <summary>Future JSON projection must preserve all 64 bits (decimal string for JavaScript consumers).</summary>
public sealed record NvidiaDrsQwordValue(ulong Value) : NvidiaDrsCurrentValue;
public sealed record NvidiaDrsUnicodeValue(string Value) : NvidiaDrsCurrentValue;
public sealed record NvidiaDrsBinaryValue(ImmutableArray<byte> Value) : NvidiaDrsCurrentValue;
