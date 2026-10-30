using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Vendors.Mechrevo;

namespace Resource_Manager_APP.Tests;

public sealed class NotebookOemFanResourceLifecycleTests
{
    private static readonly CompiledFirmwareProviderPlan Supported = new(FirmwareIdentitySnapshot.Unknown,
        NotebookOemFanProviderKind.MechrevoUwAcpi);

    [Fact]
    public void LastDemandReleasesAndNextExplicitReadRecreatesTheAuthoritativeSdkReader()
    {
        var sessions = new List<RecordedSession>();
        var paths = new List<string>();
        var selectedPath = "first.dll";
        using var zone = CreateZone(() => Supported, () => selectedPath, path =>
        {
            paths.Add(path);
            var session = new RecordedSession(_ => 0);
            sessions.Add(session);
            return session;
        });
        zone.SynchronizeDemand(true);
        Assert.Empty(sessions);
        Assert.Equal(0, zone.ReadSensors().CpuFanSpeedRpm);
        zone.SynchronizeDemand(true);
        zone.ReadSensors();
        Assert.Single(sessions);
        Assert.Equal(0, sessions[0].Releases);
        Assert.Equal(12, sessions[0].Reads);
        zone.SynchronizeDemand(false);
        zone.SynchronizeDemand(false);
        Assert.Equal(1, sessions[0].Releases);
        selectedPath = "second.dll";
        zone.SynchronizeDemand(true);
        Assert.Single(sessions);
        zone.ReadSensors();
        Assert.Equal(new[] { "first.dll", "second.dll" }, paths);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(0, sessions[1].Releases);
        zone.Dispose();
        zone.Dispose();
        Assert.Equal(1, sessions[1].Releases);
        Assert.Throws<ObjectDisposedException>(() => zone.ReadSensors());
        Assert.Equal(2, sessions.Count);
    }



    [Fact]
    public void RetiringTheConfiguredFirmwareProviderReleasesWithoutChangingTheReadImplementation()
    {
        var plan = Supported;
        var sessions = new List<RecordedSession>();
        using var zone = CreateZone(() => plan, () => "retire.dll", _ =>
        {
            var session = new RecordedSession(_ => 0); sessions.Add(session); return session;
        });
        zone.ReadSensors();
        plan = CompiledFirmwareProviderPlan.Default;
        zone.SynchronizeDemand(true);
        Assert.Equal(1, sessions[0].Releases);
        Assert.Equal(HardwareSensorProviderStates.UnsupportedFirmware, zone.ReadSensors().ProviderState.State);
        Assert.Single(sessions);
        plan = Supported;
        zone.ReadSensors();
        Assert.Equal(2, sessions.Count);
        plan = CompiledFirmwareProviderPlan.Default;
        Assert.Equal(HardwareSensorProviderStates.UnsupportedFirmware, zone.ReadSensors().ProviderState.State);
        Assert.Equal(1, sessions[1].Releases);
    }

    [Fact]
    public void FailedLoadRemainsOneAttemptPerDemandLifetimeAndCanRetryAfterRelease()
    {
        var paths = 0;
        var opens = 0;
        using var zone = CreateZone(() => Supported, () => { paths++; return "missing.dll"; }, _ => { opens++; return null; });
        zone.ReadSensors();
        zone.ReadSensors();
        Assert.Equal(1, paths);
        Assert.Equal(1, opens);
        zone.SynchronizeDemand(false);
        zone.SynchronizeDemand(true);
        Assert.Equal(1, opens);
        zone.ReadSensors();
        Assert.Equal(2, paths);
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task LastDemandReleaseWaitsForInFlightReadAndCannotFreeItsSessionEarly()
    {
        using var entered = new ManualResetEventSlim();
        using var continueRead = new ManualResetEventSlim();
        using var releaseRequested = new ManualResetEventSlim();
        var session = new RecordedSession(_ =>
        {
            entered.Set();
            if (!continueRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return 0;
        });
        using var zone = CreateZone(() => Supported, () => "in-flight.dll", _ => session);
        var read = Task.Run(zone.ReadSensors);
        Task? release = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            release = Task.Run(() => { releaseRequested.Set(); zone.SynchronizeDemand(false); });
            Assert.True(releaseRequested.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref session.Releases));
        }
        finally
        {
            continueRead.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
            if (release is not null) await release.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(6, session.Reads);
        Assert.Equal(1, session.Releases);
    }

    [Fact]
    public void SamplerUsesCatalogSourceBindingsAndReleasesWhenOnlyUnrelatedDemandRemainsOrSourceRetires()
    {
        var sessions = new List<RecordedSession>();
        using var zone = CreateZone(() => Supported, () => "catalog.dll", _ =>
        {
            var session = new RecordedSession(_ => 0); sessions.Add(session); return session;
        });
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, zone);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var fanHandles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds(["cpu.fanRpm"]), catalog);
        Assert.Contains(catalog.RuleBindings.Values, rule => rule.SourceId == NativeMetricSnapshotSourceCatalog.NotebookOemFan
            && fanHandles.Contains(rule.MetricHandle));
        zone.ReadSensors();
        sampler.SynchronizePlatformSensorDemand(fanHandles, catalog);
        Assert.Equal(0, sessions[0].Releases);
        var cpuHandles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds(["cpu.usage"]), catalog);
        sampler.SynchronizePlatformSensorDemand(cpuHandles, catalog);
        Assert.Equal(1, sessions[0].Releases);
        zone.ReadSensors();
        var retired = catalog with { RuleBindings = catalog.RuleBindings.Where(pair => pair.Value.SourceId != NativeMetricSnapshotSourceCatalog.NotebookOemFan).ToImmutableDictionary() };
        sampler.SynchronizePlatformSensorDemand(fanHandles, retired);
        Assert.Equal(1, sessions[1].Releases);
    }

    [Fact]
    public void ExistingWorkerCleanupCallbackReleasesEvenWithoutAPublishedCatalogOrCounterBaseline()
    {
        var session = new RecordedSession(_ => 0);
        using var zone = CreateZone(() => Supported, () => "cleanup.dll", _ => session);
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, zone);
        zone.ReadSensors();
        typeof(WindowsHardwareMetricSampler).GetMethod("SynchronizeCounterDemand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sampler, [Array.Empty<string>()]);
        Assert.Equal(1, session.Releases);
    }

    private static NotebookOemFanMonitoringZone CreateZone(Func<CompiledFirmwareProviderPlan> plan,
        Func<string?> path, Func<string, IUwAcpiReadSession?> open)
        => new(plan, new NotebookOemFanSensorReader(new MechrevoUwAcpiSensorReader(path,
            library => new MechrevoUwAcpiReader(library, open))));

    private static WindowsHardwareMetricSampler CreateSampler(HostManagerSamplingSubscriptionTestFixture subscriptions,
        NotebookOemFanMonitoringZone zone)
        => new(cpuZone: null!, memoryZone: null!, virtualMemoryZone: null!, gpuAdapterOrderZone: null!, cpuFrequencyZone: null!,
            gpuEngineZone: null!, systemIoZone: null!, nvidiaZone: null!, nvidiaNvapiZone: null!, amdAdlxZone: null!, amdSmuZone: null!,
            integratedGpuSensorRoute: new AmdIntegratedGpuSensorRoute(), platformSensorReader: new(null!, null!, zone),
            metricSnapshotOwner: null!, metricCatalog: new HardwareMetricCatalogState(),
 samplingSubscriptionOwner: subscriptions.Owner,
            runtimePlanProvider: subscriptions.Provider, logger: NullLogger<WindowsHardwareMetricSampler>.Instance);

    private sealed class RecordedSession(Func<int, int> read) : IUwAcpiReadSession
    {
        public int Reads;
        public int Releases;
        public int ReadEc(int address)
        {
            Assert.Equal(0, Volatile.Read(ref Releases));
            Interlocked.Increment(ref Reads);
            return read(address);
        }
        public void Dispose() => Interlocked.Increment(ref Releases);
    }
}
