using LibreHardwareMonitor.Hardware;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class MonitoringCoreAndNvapiLifetimeTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(1, 1, 1, 1, 20_000);
    private static readonly NvidiaDrsProfileRequest[] Selected = [new("Game", new uint[] { 7 })];

    [Fact]
    public async Task NvapiFreezeReturnsOwnedApiAndOnlyRealReadsAcquireTheNextInjectedApi()
    {
        var apis = new List<NvidiaDrsHostRecording>();
        using var zone = new NvidiaNvapiMonitoringZone(new NvidiaNvapiReader(() =>
        {
            var api = new NvidiaDrsHostRecording(); apis.Add(api); return api;
        }));
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(apis);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.NotNull(zone.ReadDrs(Selected, Limits, default));
            Assert.NotNull(zone.DiscoverDrs(Limits, default));
            Assert.Equal(cycle + 1, apis.Count);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            Assert.All(apis, api => { Assert.Equal(1, api.Unloads); Assert.Equal(1, api.Frees); Assert.False(api.Live); });
            Assert.Null(zone.ReadDrs(Selected, Limits, default));
            Assert.Null(zone.DiscoverDrs(Limits, default));
            Assert.Empty(await zone.ReadAsync(NvidiaNvapiReadRequest.All, null!, default));
            Assert.Equal(cycle + 1, apis.Count);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
            Assert.Equal(cycle + 1, apis.Count);
        }
    }

    [Fact]
    public void NvapiReleaseFailureRetainsPendingSessionAndModuleUntilExplicitRetry()
    {
        var api = new NvidiaDrsHostRecording { FailDestroy = true };
        var creations = 0;
        using var reader = new NvidiaNvapiReader(() => { creations++; return api; });
        Assert.Null(reader.ReadDrs(Selected, Limits));
        Assert.Throws<InvalidOperationException>(reader.ReleaseSession);
        Assert.True(api.Live); Assert.Equal(0, api.Unloads); Assert.Equal(0, api.Frees);
        Assert.Null(reader.ReadDrs(Selected, Limits));
        Assert.Equal(1, creations); Assert.Equal(1, api.Creates);
        api.FailDestroy = false; api.FailUnload = true;
        Assert.Throws<InvalidOperationException>(reader.ReleaseSession);
        Assert.False(api.Live); Assert.Equal(0, api.Frees);
        api.FailUnload = false;
        reader.ReleaseSession(); reader.ReleaseSession();
        Assert.Equal(2, api.Unloads); Assert.Equal(1, api.Frees);
        Assert.NotNull(reader.ReadDrs(Selected, Limits));
        Assert.Equal(2, creations);
    }

    [Fact]
    public async Task NvapiFreezeDrainsAdmittedNativeReadBeforeReturningItsApi()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var freezeStarted = new ManualResetEventSlim();
        var api = new NvidiaDrsHostRecording { OnSetting = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        } };
        using var zone = new NvidiaNvapiMonitoringZone(new NvidiaNvapiReader(api));
        var read = Task.Run(() => zone.ReadDrs(Selected, Limits, default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task freeze;
        try
        {
            freeze = Task.Run(() => { freezeStarted.Set(); zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze); });
            Assert.True(freezeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(freeze.Wait(TimeSpan.FromMilliseconds(30)));
            Assert.Equal(0, api.Unloads);
        }
        finally { release.Set(); }
        await read; await freeze;
        Assert.Equal(1, api.Unloads); Assert.Equal(1, api.Frees);
        Assert.Null(zone.ReadDrs(Selected, Limits, default));
        Assert.Equal(1, api.Creates);
    }

    [Fact]
    public void CoreFreezeReturnsComputerAndRealReadsReopenWithoutDiscardingFailureCache()
    {
        var sessions = new List<Session>();
        using var zone = new HardwareMonitorWmiMonitoringZone(new HardwareMonitorCoreReader(() =>
        {
            var session = new Session(); sessions.Add(session); return session;
        }));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(zone.ReadSensors().CatalogComplete);
            _ = zone.ReadFanCatalog(1);
            Assert.Equal(cycle + 1, sessions.Count);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            Assert.All(sessions, session => Assert.Equal(1, session.Closes));
            Assert.Equal(HardwareSensorProviderStates.Frozen, zone.ReadSensors().ProviderState.State);
            _ = zone.ReadFanCatalog(1);
            Assert.Equal(cycle + 1, sessions.Count);
            zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
            Assert.Equal(cycle + 1, sessions.Count);
        }
        var failedOpens = 0;
        using var failed = new HardwareMonitorCoreReader(() =>
        {
            failedOpens++; return new Session { FailOpen = true };
        });
        var original = failed.Read(TimeSpan.FromSeconds(1)).ProviderState;
        failed.ReleaseSession();
        Assert.Equal(original, failed.Read(TimeSpan.FromSeconds(1)).ProviderState);
        Assert.Equal(1, failedOpens);
    }

    [Fact]
    public void CoreFailedCloseRemainsOwnedForExplicitRetryIncludingTerminalDispose()
    {
        var session = new Session { FailClose = true };
        var creations = 0;
        var reader = new HardwareMonitorCoreReader(() => { creations++; return session; });
        _ = reader.Read(TimeSpan.FromSeconds(1));
        Assert.Throws<IOException>(reader.ReleaseSession);
        Assert.Throws<IOException>(reader.Dispose);
        Assert.Equal(1, creations); Assert.Equal(2, session.Closes);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(TimeSpan.FromSeconds(1)));
        session.FailClose = false;
        reader.Dispose(); reader.Dispose();
        Assert.Equal(3, session.Closes);
    }

    private sealed class Session : IHardwareMonitorCoreSession
    {
        internal int Closes;
        internal bool FailOpen, FailClose;
        public IList<IHardware> Hardware => Array.Empty<IHardware>();
        public void Open() { if (FailOpen) throw new IOException("recorded open failure"); }
        public void Close() { Closes++; if (FailClose) throw new IOException("recorded close failure"); }
    }
}
