using System.Runtime.InteropServices;
using System.Security;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsVolumeSpaceInformationApi
{
    IReadOnlyList<string>? EnumerateVolumeRoots();
    int ReadSpace(string root, out WindowsVolumeSpaceNativeData space, out string? callerSid);
}

public sealed class WindowsVolumeSpaceInformationReader
{
    private readonly IWindowsVolumeSpaceInformationApi api;
    private readonly IWindowsVolumeMetadataInformationApi? metadataApi;
    internal WindowsVolumeSpaceInformationReader(IWindowsVolumeSpaceInformationApi api, IWindowsVolumeMetadataInformationApi? metadataApi = null)
    { this.api = api; this.metadataApi = metadataApi; }
    internal const int MaximumVolumes = 4096;
    public WindowsVolumeSpaceInformationReader() : this(new WindowsVolumeSpaceInformationNativeApi(), new WindowsVolumeMetadataInformationNativeApi()) { }

    public IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? Read()
    {
        IReadOnlyList<string>? roots;
        try { roots = api.EnumerateVolumeRoots(); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
        if (roots is null || roots.Count > MaximumVolumes) return null;
        var volumes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!TryVolumeIdentity(root, out var identity, out var canonicalRoot) || !volumes.TryAdd(identity, canonicalRoot))
                return null;
        }
        var result = new List<DeviceTopologyVolumeSpaceInformation>(volumes.Count);
        foreach (var (identity, root) in volumes)
        {
            DeviceTopologyVolumeSpaceInformation volume;
            try
            {
                volume = api.ReadSpace(root, out var data, out var sid) == 0
                    ? Create(identity, data, sid) : new(identity);
            }
            catch (Exception error) when (IsSourceFailure(error)) { volume = new(identity); }
            if (metadataApi is not null)
                volume = volume with {
                    DiskExtents = ReadSource(() => metadataApi.ReadDiskExtents(root)),
                    MountPaths = ReadSource(() => metadataApi.ReadMountPaths(root)),
                    FileSystem = ReadSource(() => metadataApi.ReadFileSystem(root))
                };
            result.Add(volume);
        }
        return result.AsReadOnly();
    }

    public Information.DeviceInformationSnapshot ReadFields()
    {
        var rows = Read();
        return Information.WindowsInformationFieldProjection.Volumes(rows, rows is null ? null : DateTimeOffset.UtcNow);
    }

    private static T? ReadSource<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (IsSourceFailure(error)) { return null; }
    }

    internal static DeviceTopologyVolumeSpaceInformation Create(string identity, WindowsVolumeSpaceNativeData data, string? sid)
    {
        ulong? unit = data.SectorsPerAllocationUnit > 0 && data.BytesPerSector > 0
            ? (ulong)data.SectorsPerAllocationUnit * data.BytesPerSector : null;
        UInt128? Bytes(ulong count) => unit is { } bytes ? (UInt128)count * bytes : null;
        var caller = string.IsNullOrWhiteSpace(sid) ? null : sid;
        return new(identity)
        {
            QueryCallerSid = caller,
            TotalBytes = Bytes(data.ActualTotalAllocationUnits),
            AvailableBytes = Bytes(data.ActualAvailableAllocationUnits),
            PoolUnavailableBytes = Bytes(data.ActualPoolUnavailableAllocationUnits),
            CallerTotalBytes = caller is null ? null : Bytes(data.CallerTotalAllocationUnits),
            CallerAvailableBytes = caller is null ? null : Bytes(data.CallerAvailableAllocationUnits),
            CallerPoolUnavailableBytes = caller is null ? null : Bytes(data.CallerPoolUnavailableAllocationUnits),
            UsedBytes = Bytes(data.UsedAllocationUnits),
            ReservedBytes = Bytes(data.TotalReservedAllocationUnits),
            StorageReserveBytes = Bytes(data.VolumeStorageReserveAllocationUnits),
            AvailableCommittedBytes = Bytes(data.AvailableCommittedAllocationUnits),
            PoolAvailableBytes = Bytes(data.PoolAvailableAllocationUnits),
            SectorsPerAllocationUnit = data.SectorsPerAllocationUnit == 0 ? null : data.SectorsPerAllocationUnit,
            BytesPerSector = data.BytesPerSector == 0 ? null : data.BytesPerSector,
            AllocationUnitBytes = unit
        };
    }

    internal static bool TryVolumeIdentity(string? root, out string identity, out string canonicalRoot)
    {
        identity = canonicalRoot = string.Empty;
        const string prefix = @"\\?\Volume{";
        if (root is null || root.Length != prefix.Length + 38
            || !root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !root.EndsWith("}\\", StringComparison.Ordinal)
            || !Guid.TryParseExact(root.AsSpan(prefix.Length, 36), "D", out var guid)) return false;
        identity = "windows-volume|" + guid.ToString("D").ToUpperInvariant();
        canonicalRoot = prefix + guid.ToString("D").ToUpperInvariant() + "}\\";
        return true;
    }

    internal static bool IsSourceFailure(Exception error) => error is ExternalException or IOException or UnauthorizedAccessException
        or SecurityException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
}
