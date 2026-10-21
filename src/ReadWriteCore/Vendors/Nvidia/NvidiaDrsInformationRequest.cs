using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public delegate NvidiaDrsSnapshot? NvidiaDrsReadSource(IReadOnlyList<NvidiaDrsProfileRequest> profiles,
    NvidiaDrsReadLimits limits, CancellationToken cancellationToken);
public delegate NvidiaDrsSnapshot? NvidiaDrsDirectorySource(NvidiaDrsReadLimits limits, CancellationToken cancellationToken);

/// <summary>Immutable, explicit selection. Empty means no demand; full database enumeration is a separate API.</summary>
public sealed class NvidiaDrsInformationRequest
{
    public ImmutableArray<NvidiaDrsProfileRequest> Profiles { get; }
    public NvidiaDrsReadLimits Limits { get; }
    public ImmutableArray<FieldReference> Fields { get; }

    public NvidiaDrsInformationRequest(IReadOnlyList<NvidiaDrsProfileRequest> profiles, NvidiaDrsReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(limits);
        NvidiaNvapiReader.ValidateDrsLimits(limits);
        Profiles = NvidiaNvapiReader.CopyDrsRequests(profiles, limits);
        Limits = limits;
        Fields = [.. Profiles.SelectMany(profile => profile.SettingIds.Select(id =>
            new FieldReference(Identity(profile.ProfileName, id), DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue.Name)))];
    }

    public static string Identity(string profileName, uint settingId)
        => new NvidiaDrsProfile(profileName, null, null, null, null, []).DeviceIdentity
            + "|setting|" + settingId.ToString("X8");

    internal NvidiaDrsInformationRequest Select(ImmutableArray<InformationFieldSelection> selection)
    {
        if (selection.Length > Fields.Length) throw new ArgumentOutOfRangeException(nameof(selection));
        var allowed = Fields.ToHashSet();
        var requested = new HashSet<FieldReference>();
        foreach (var item in selection)
            if (item.Definition != DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue
                || !allowed.Contains(item.Field) || !requested.Add(item.Field))
                throw new ArgumentException("Selection is outside the exact DRS scope or duplicates a field.", nameof(selection));
        return new([.. Profiles.Select(profile => new NvidiaDrsProfileRequest(profile.ProfileName,
            profile.SettingIds.Where(id => requested.Contains(new(Identity(profile.ProfileName, id),
                DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue.Name))).ToImmutableArray()))], Limits);
    }
}
