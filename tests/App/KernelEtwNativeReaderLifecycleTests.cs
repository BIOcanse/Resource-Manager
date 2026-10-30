using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

/// <summary>Product owners over actual native command queues, immutable banks and resource holds.</summary>
public sealed class KernelEtwNativeReaderLifecycleTests
{
    private static CpuResidencyCaptureConfiguration CpuConfiguration() => new(1000, 32,
        new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "ccd:0", 1, 1) }.ToFrozenDictionary(),
        "controlled native fixture", 50);

    [Fact]
    public void CpuReadAndDiagnosticRegistrationAcquireNoRunOrDurationHold()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, CpuConfiguration);
        using var request = owner.RequestThreadDiagnostics(100, 1000);
        for (var i = 0; i < 10; i++) { Assert.Null(owner.Read()); Assert.Null(request.ReadWindow(owner.Read())); }
        Assert.Equal(0, broker.Acquires); Assert.Equal(0, broker.DurationAcquires);
        Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
    }

    [Fact]
    public void FiniteCpuWindowReturnsOnlyDurationAndResumesOnTheSameMetadataGeneration()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, CpuConfiguration);
        owner.EnsureStarted();
        Assert.False(owner.IsWindowActive);
        owner.BeginWindow();
        Assert.True(owner.IsWindowActive);
        var generation = broker.GetSnapshot().Generation;
        var nativeSession = broker.Resource.Session;
        var end = NativeCpuReaderTestFeed.ClosedWindow(owner, [0], 1000);
        Assert.True(owner.Collect());
        var first = Assert.IsType<CpuCoreResidencySnapshot>(owner.Read());
        Assert.True(Assert.Single(first.Processes).ExecutionTimeMilliseconds > 0);
        owner.PauseWindow();
        Assert.Equal(CpuResidencyHeldTestBroker.MetadataKeywords, broker.Keywords);
        Assert.Equal(0, broker.DurationCount);
        for (var i = 0; i < 10; i++) Assert.Same(first, owner.Read());
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(1, broker.MetadataAcquires); Assert.Equal(1, broker.DurationAcquires);
        owner.BeginWindow();
        Assert.Same(nativeSession, broker.Resource.Session);
        Assert.Equal(generation, broker.GetSnapshot().Generation);
        Assert.Equal(1, broker.SessionStarts);
        Assert.Equal(1, broker.DurationCount);
        Assert.True(owner.Collect()); Assert.Null(owner.Read());
        NativeCpuReaderTestFeed.ClosedWindow(owner, [0], 1000, previousEnd: end);
        Assert.True(owner.Collect()); Assert.NotNull(owner.Read());
        owner.Stop();
        Assert.Equal(0, broker.ActiveCount); Assert.Equal(0, broker.DurationCount);
        Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
    }

    [Fact]
    public void LateCpuBindingCannotAcceptAnOldCompleteBankBeforeItsOwnTrackedReset()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        using var first = new EtwCpuResidencyCaptureOwner(broker, CpuConfiguration);
        first.EnsureStarted();
        Assert.False(first.IsWindowActive);
        first.BeginWindow();
        Assert.True(first.IsWindowActive);
        var end = NativeCpuReaderTestFeed.ClosedWindow(first, [0], 1000);
        Assert.True(first.Collect()); Assert.NotNull(first.Read());
        using var later = new EtwCpuResidencyCaptureOwner(broker, CpuConfiguration);
        later.EnsureStarted();
        Assert.False(later.IsWindowActive);
        later.BeginWindow();
        Assert.True(later.IsWindowActive);
        // The native bank is still the earlier complete bank: this reader's
        // map/config/reset is staged, and its required ticket is not acknowledged.
        Assert.True(later.Collect()); Assert.Null(later.Read());
        broker.Resource.Publish(broker.Resource.Tick);
        Assert.True(later.Collect()); Assert.Null(later.Read());
        NativeCpuReaderTestFeed.ClosedWindow(later, [0], 1000, previousEnd: end);
        Assert.True(later.Collect()); Assert.NotNull(later.Read());
    }

    [Fact]
    public void FatalNativeFailureCannotReuseCpuCurrentWhileBrokerStillReportsRunning()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, CpuConfiguration);
        owner.EnsureStarted();
        Assert.False(owner.IsWindowActive);
        owner.BeginWindow();
        Assert.True(owner.IsWindowActive);
        NativeCpuReaderTestFeed.ClosedWindow(owner, [0], 1000);
        Assert.True(owner.Collect()); Assert.NotNull(owner.Read());
        broker.Resource.RecordNativeFailureWithoutCompletion(3);
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.False(owner.Collect());
        Assert.Equal(CpuCoreResidencyUnavailableReasons.CaptureFailed,
            Assert.IsType<CpuCoreResidencyUnavailable>(owner.ReadObservation()).ReasonCode);
    }

    [Fact]
    public void InterruptIdlePublicationExpiresTotalsWithoutRestartingTheHeldSession()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var source = InterruptSource(resource, sampling);
        Invoke(source, "EnsureStarted");
        resource.Publish(0);
        var nativeSession = resource.Session;
        var generation = resource.Generation;
        resource.Image(1, 0xFFFF_F800_0000_1000UL, 0x100, @"C:\Windows\System32\drivers\driver.sys");
        Assert.Same(nativeSession, resource.Session); Assert.Equal(generation, resource.Generation);
        resource.Interrupt(500, 2, 4.5, 0xFFFF_F800_0000_1010UL);
        resource.Publish(10000);
        Assert.True(Publish(source, resource));
        var first = source.Read();
        Assert.Equal(1, first.EventCount);
        Assert.Equal("driver.sys", Assert.Single(first.Drivers).ModuleName);
        Assert.Same(first, source.Read());
        resource.Publish(71001); // No event is required for the consumer to age this window.
        Assert.True(Publish(source, resource));
        Assert.Equal(0, source.Read().EventCount); Assert.Empty(source.Read().Drivers);
        Assert.Equal(1, resource.Creations);
    }

    [Fact]
    public async Task FatalNativeFailureCannotReuseInterruptCurrentWhileBrokerStillReportsRunning()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var source = InterruptSource(resource, sampling);
        Invoke(source, "EnsureStarted"); resource.Publish(0);
        resource.Interrupt(1, 1, 2, 0x1010); resource.Publish(10000);
        Assert.True(Publish(source, resource)); Assert.Equal(1, source.Read().EventCount);
        resource.RecordNativeFailureWithoutCompletion(3); Assert.True(resource.GetSnapshot().IsRunning);
        var operation = (Task<bool>)Invoke(source, "TryPublishCurrentSnapshotAsync", DateTimeOffset.UtcNow,
            new SamplingOwnerToken(1, 1), TimeSpan.FromSeconds(1), CancellationToken.None)!;
        Assert.False(await operation);
        Assert.Equal(SystemInterruptProviderStates.Unavailable, source.Read().ProviderState.State);
        Assert.Equal(0, source.Read().EventCount);
    }

    private static EtwSystemInterruptSnapshotSource InterruptSource(NativeKernelEtwTestResource resource,
        HostManagerSamplingSubscriptionTestFixture sampling)
        => new(resource, sampling.Owner, NullLogger<EtwSystemInterruptSnapshotSource>.Instance,
            () => resource.Session.GetPublicationTimestamp(resource.Tick), () => resource.Tick);
    private static bool Publish(EtwSystemInterruptSnapshotSource source, NativeKernelEtwTestResource resource)
        => (bool)Invoke(source, "TryPublishCurrentSnapshot", resource.Session.GetPublicationTimestamp(resource.Tick), new SamplingOwnerToken(1, 1))!;
    private static object? Invoke(EtwSystemInterruptSnapshotSource source, string method, params object[] args)
        => typeof(EtwSystemInterruptSnapshotSource).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, args);
}

