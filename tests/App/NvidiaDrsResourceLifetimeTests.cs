using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using System.Runtime.InteropServices;
using System.Text;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsResourceLifetimeTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(1, 1, 1, 1, 20_000);

    [Fact]
    public async Task CleanupFailureCannotPublishSuccessAndOriginalOwnedResourcesRetainTheObligation()
    {
        var api = new Api(); var reader = new NvidiaNvapiReader(api);
        var resources = Resources(reader); var otherReleases = 0;
        resources.Own(new Release(() => otherReleases++)); resources.Own(reader);
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7 })], Limits);
        var access = owner.CreateNvidiaDrsInformation(scope, resources.ReadNvapiDrs);
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(scope.Fields[0]).Status);
        api.FailDestroy = true;
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(scope.Fields[0]).Status);
        Assert.Equal(2, api.Creates); Assert.True(api.SessionLive);
        _ = await access.Backend.CollectAsync(scope.Fields, default);
        Assert.Equal(2, api.Creates); // Pending session blocks another Create, not an accumulating session list.
        Assert.Throws<AggregateException>(resources.Dispose);
        Assert.Equal(1, otherReleases); Assert.Equal(0, api.Unloads); Assert.Equal(0, api.Frees);
        api.FailDestroy = false; api.FailUnload = true;
        Assert.Throws<AggregateException>(resources.Dispose);
        Assert.False(api.SessionLive); Assert.Equal(1, api.Unloads); Assert.Equal(0, api.Frees);
        api.FailUnload = false; resources.Dispose(); resources.Dispose();
        Assert.Equal(2, api.Unloads); Assert.Equal(1, api.Frees); Assert.Equal(1, otherReleases);
        Assert.Throws<ObjectDisposedException>(() => resources.ReadNvapiDrs(scope.Profiles, Limits, default));
    }

    [Fact]
    public void BorrowedCallbacksAreNeverDisposedByTheResourceBundle()
    {
        var api = new Api(); using var reader = new NvidiaNvapiReader(api);
        var resources = Resources(reader); resources.Dispose();
        Assert.Equal(0, api.Unloads); Assert.Equal(0, api.Frees);
        Assert.NotNull(resources.ReadNvapiDrs([new("Game", new uint[] { 7 })], Limits, default));
        Assert.Equal(1, api.Creates);
        Assert.Same(reader, resources.ReadNvapiDrs.Target);
        Assert.Same(reader, resources.DiscoverNvapiDrs.Target);
        Assert.Same(reader, resources.ReadNvapiAsync.Target);
    }

    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    private static WindowsHardwareMetricResources Resources(NvidiaNvapiReader reader) => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded",
        ReadNvapiDrs = reader.ReadDrs, DiscoverNvapiDrs = reader.DiscoverDrs, ReadNvapiAsync = reader.ReadAsync,
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => throw Unexpected(), ReadNumaIdentities = () => throw Unexpected(),
        ReadGpuInventory = () => throw Unexpected(), ReadCpuFrequency = () => throw Unexpected(), ReadGpuUsage = _ => throw Unexpected(),
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadAdlxAsync = (_, _, _) => throw Unexpected(),
        ReadAmdSmu = _ => throw Unexpected(), ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBatteryIdentities = () => throw Unexpected(), ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = () => throw Unexpected(), ReadNetworkInterfaces = (_, _) => throw Unexpected(),
        ReadSystemIo = _ => throw Unexpected(), ReadStorageIdentities = _ => throw Unexpected(),
        ReadFanIdentities = _ => throw Unexpected(), ReadStorageSensors = () => throw Unexpected(),
        ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(),
        ReadHardwareMonitor = () => throw Unexpected(), ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = _ => { }
    };
    private static Exception Unexpected() => new InvalidOperationException("Unselected recorded source.");

    private sealed class Api : INvidiaNvapiDrsApi
    {
        internal int Creates, Unloads, Frees;
        internal bool FailDestroy, FailUnload, SessionLive;
        public bool IsSupported => true;
        public int Initialize() => 0;
        public int Unload() { Assert.False(SessionLive); Unloads++; return FailUnload ? -1 : 0; }
        public void FreeLibrary() { Assert.False(SessionLive); Frees++; }
        public int CreateSession(out nint session) { Assert.False(SessionLive); SessionLive = true; session = ++Creates; return 0; }
        public int DestroySession(nint session) { Assert.True(SessionLive); if (FailDestroy) return -1; SessionLive = false; return 0; }
        public int LoadSettings(nint session) => 0;
        public int FindProfileByName(nint session, string name, out nint profile) { Assert.Equal("Game", name); profile = 1; return 0; }
        public int GetProfileInfo(nint session, nint profile, ref NvDrsProfile info)
        {
            var bytes = new byte[Marshal.SizeOf<NvDrsProfile>()];
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(info, pin.AddrOfPinnedObject(), false);
                var name = Encoding.Unicode.GetBytes("Game\0");
                Marshal.Copy(name, 0, pin.AddrOfPinnedObject() + 4, name.Length);
                info = Marshal.PtrToStructure<NvDrsProfile>(pin.AddrOfPinnedObject());
            }
            finally { pin.Free(); }
            info.SettingCount = 1; return 0;
        }
        public int GetSetting(nint session, nint profile, uint id, ref NvDrsSetting setting)
        { setting.Id = id; setting.Type = 0; setting.Current.Dword = 0; return 0; }
        public int GetNumProfiles(nint session, out uint count) => throw Unexpected();
        public int EnumProfiles(nint session, uint index, out nint profile) => throw Unexpected();
        public int EnumSettings(nint session, nint profile, uint start, ref uint count, nint settings) => throw Unexpected();
    }
}
