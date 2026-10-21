using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed record WindowsDiskInterface(string DevicePath, string PnpInstanceId);
internal sealed record WindowsDiskLayoutCapture(uint DiskNumber, DeviceTopologyDiskLayout? Layout)
{
    internal DiskAccessAlignmentInformation? AccessAlignment { get; init; }
    internal DiskHardwareIdentification? HardwareIdentification { get; init; }
    internal uint? BusTypeReported { get; init; }
    internal bool? TrimEnabledReported { get; init; }
}
internal interface IWindowsDiskLayoutInformationApi
{
    IReadOnlyList<WindowsDiskInterface>? Enumerate();
    WindowsDiskLayoutCapture? Read(WindowsDiskInterface device);
    WindowsDiskLayoutCapture? Read(WindowsDiskInterface device, bool includeTrim) => Read(device);
    Task<WindowsDiskPerformanceNativeRead?> ReadPerformanceAsync(WindowsDiskInterface device,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]> candidates,
        IReadOnlySet<string> selectedIdentityKeys, TimeSpan sampleWindow, TimeProvider clock, CancellationToken cancellationToken)
        => Task.FromResult<WindowsDiskPerformanceNativeRead?>(null);
}

public sealed class WindowsDiskLayoutInformationReader
{
    private readonly IWindowsDiskLayoutInformationApi api;
    internal WindowsDiskLayoutInformationReader(IWindowsDiskLayoutInformationApi api) => this.api = api;
    internal const int MaximumDisks = 4096;
    public WindowsDiskLayoutInformationReader() : this(new WindowsDiskLayoutInformationNativeApi()) { }
    public IReadOnlyList<DeviceTopologyDiskLayoutInformation>? Read() => Read(includeTrim: true);
    public IReadOnlyList<DeviceTopologyDiskLayoutInformation>? Read(bool includeTrim)
    {
        var entries = EnumerateInterfaces();
        if (entries is null) return null;
        var result = new List<DeviceTopologyDiskLayoutInformation>(entries.Count);
        foreach (var entry in entries)
        {
            WindowsDiskLayoutCapture? data = null;
            try { data = api.Read(entry, includeTrim); }
            catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { }
            var identity = "windows-pnp|" + entry.PnpInstanceId.Trim().ToUpperInvariant();
            result.Add(new(identity) { OsDiskNumberReported = data?.DiskNumber, Layout = data?.Layout,
                AccessAlignment = data?.AccessAlignment, HardwareIdentification = data?.HardwareIdentification,
                BusTypeReported = data?.BusTypeReported, TrimEnabledReported = data?.TrimEnabledReported });
        }
        return result.AsReadOnly();
    }

    internal IReadOnlyList<WindowsDiskInterface>? EnumerateInterfaces()
    {
        IReadOnlyList<WindowsDiskInterface>? directory;
        try { directory = api.Enumerate(); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { return null; }
        if (directory is null || directory.Count > MaximumDisks) return null;
        var entries = new Dictionary<string, WindowsDiskInterface>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in directory)
        {
            if (string.IsNullOrWhiteSpace(entry.PnpInstanceId) || string.IsNullOrWhiteSpace(entry.DevicePath)
                || entry.PnpInstanceId.Any(char.IsControl) || entry.DevicePath.Any(char.IsControl)) return null;
            var identity = "windows-pnp|" + entry.PnpInstanceId.Trim().ToUpperInvariant();
            if (!entries.TryAdd(identity, entry) || !paths.Add(entry.DevicePath)) return null;
        }
        return entries.Values.ToArray();
    }

    internal Task<WindowsDiskPerformanceNativeRead?> ReadPerformanceAsync(WindowsDiskInterface device,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]> candidates,
        IReadOnlySet<string> selectedIdentityKeys, TimeSpan sampleWindow, TimeProvider clock, CancellationToken cancellationToken)
        => api.ReadPerformanceAsync(device, candidates, selectedIdentityKeys, sampleWindow, clock, cancellationToken);

    public Information.DeviceInformationSnapshot ReadFields(IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes = null,
        IReadOnlyList<DeviceTopologyStorageInformation>? storage = null)
    {
        var rows = Read();
        return Information.WindowsInformationFieldProjection.Disks(rows, volumes, storage, rows is null ? null : DateTimeOffset.UtcNow);
    }
}
