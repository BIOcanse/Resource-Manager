using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class TopologySingleFetchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CpuSingleFetchUsesOriginalWorkerRejectsOldCaptureAndReleasesLastDemand()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var pending = fixture.Reader.FetchOnceAsync("cpu-single-fetch", CancellationToken.None);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        Assert.False(pending.IsCompleted);
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(12) with
            { CapturedAt = DateTimeOffset.UnixEpoch });
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        Assert.False(pending.IsCompleted);
        var fresh = CpuTopologyProviderTestFixture.CreateSnapshot(34);
        fixture.Sampler.Complete(fresh);
        Assert.Same(fresh, await pending.WaitAsync(Deadline));
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public async Task CpuInformationFreshFailureClearsFieldsAndReleasesLastDemand()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var pending = fixture.Reader.FetchInformationOnceAsync("cpu-information-once", CancellationToken.None);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(new InvalidOperationException("Recorded fresh attempt failed."));
        var failed = await pending.WaitAsync(Deadline);
        Assert.NotNull(failed);
        Assert.Null(failed.ObservedAt);
        Assert.Empty(failed.Devices);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CpuFetchCancellationReleasesDemandBeforeWorkerStarts(bool information)
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var cancellation = new CancellationTokenSource();
        Task pending = information
            ? fixture.Reader.FetchInformationOnceAsync("cpu-information-cancel", cancellation.Token)
            : fixture.Reader.FetchOnceAsync("cpu-topology-cancel", cancellation.Token);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        Assert.Equal(0, fixture.Sampler.CaptureCount);
    }

    [Fact]
    public async Task DeviceSingleFetchIgnoresInitialRetainedStateAndAcceptsFreshFailedAttempt()
    {
        var source = new DeviceSource();
        var pending = source.FetchOnceAsync("device-state-once", CancellationToken.None);
        await source.StateStarted.Task.WaitAsync(Deadline);
        Assert.Equal(1, source.Active);
        Assert.False(pending.IsCompleted);
        var failed = source.Initial with { LastAttemptAt = DateTimeOffset.UtcNow,
            State = DeviceTopologySnapshotStatus.Failed, InformationFields = DeviceInformationSnapshot.Empty };
        source.States.Writer.TryWrite(failed);
        Assert.Same(failed, await pending.WaitAsync(Deadline));
        Assert.Equal("device-state-once", source.Holder);
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public async Task AcpiSingleFetchAcceptsPublishedFixedValueWithOriginalObservationTimeAndReleases()
    {
        var source = new DeviceSource();
        var pending = source.FetchAcpiOnceAsync("acpi-fields-once", CancellationToken.None);
        await source.AcpiStarted.Task.WaitAsync(Deadline);
        Assert.False(pending.IsCompleted);
        var fixedValue = new DeviceInformationSnapshot(DateTimeOffset.UnixEpoch, []);
        source.Acpi.Writer.TryWrite(fixedValue);
        Assert.Same(fixedValue, await pending.WaitAsync(Deadline));
        Assert.Equal("acpi-fields-once", source.Holder);
        Assert.Equal(0, source.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcpiSingleFetchWaitsForOwnerAttemptThenRepublishesItsOnceReadValue(bool failed)
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var firmware = new OnceFirmware(failed);
        using var provider = new ResourceManager.App.Infrastructure.DeviceTopology.Snapshots.DeviceTopologySnapshotProvider(
            null!, new EmptyStore(), new(), sampling.Owner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResourceManager.App.Infrastructure.DeviceTopology.Snapshots.DeviceTopologySnapshotProvider>.Instance,
            firmware);
        var first = provider.FetchAcpiOnceAsync("first-fixed-fetch", CancellationToken.None);
        Assert.False(first.IsCompleted); // The unread Empty value must never escape as a requested publication.
        await provider.SampleAcpiNowAsync(CancellationToken.None);
        var value = await first.WaitAsync(Deadline);
        Assert.NotNull(value);
        Assert.Equal(failed, value.ObservedAt is null);
        var second = provider.FetchAcpiOnceAsync("second-fixed-fetch", CancellationToken.None);
        Assert.False(second.IsCompleted); // Even a cached fixed fact needs the named-demand publication.
        await provider.SampleAcpiNowAsync(CancellationToken.None);
        Assert.Same(value, await second.WaitAsync(Deadline));
        Assert.Equal(1, firmware.Reads);
    }

    private sealed class OnceFirmware(bool failed) : ResourceManager.ReadWrite.Windows.Devices.IAcpiFirmwareTableReader
    {
        internal int Reads;
        public IReadOnlyList<ResourceManager.ReadWrite.Windows.Devices.Models.AcpiFirmwareTable>? ReadTables()
        {
            Reads++;
            return failed ? null : AcpiFirmwareTestData.Tables();
        }
    }

    private sealed class EmptyStore : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingDeviceFetchReleasesItsOnlyDemand(bool acpi)
    {
        var source = new DeviceSource();
        using var cancellation = new CancellationTokenSource();
        Task pending = acpi ? source.FetchAcpiOnceAsync("acpi-cancel", cancellation.Token)
            : source.FetchOnceAsync("device-cancel", cancellation.Token);
        await (acpi ? source.AcpiStarted.Task : source.StateStarted.Task).WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.Equal(0, source.Active);
    }

    private sealed class DeviceSource : IDeviceTopologySnapshotProvider
    {
        internal DeviceTopologySnapshotState Initial { get; } = DeviceTopologySnapshotState.Warming with
            { LastAttemptAt = DateTimeOffset.UnixEpoch, State = DeviceTopologySnapshotStatus.Ready };
        internal Channel<DeviceTopologySnapshotState> States { get; } = Channel.CreateUnbounded<DeviceTopologySnapshotState>();
        internal Channel<DeviceInformationSnapshot> Acpi { get; } = Channel.CreateUnbounded<DeviceInformationSnapshot>();
        internal TaskCompletionSource StateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AcpiStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active;
        internal string? Holder;
        public DeviceTopologySnapshotState ReadState() => throw new InvalidOperationException("A single fetch must use subscription demand.");
        public async IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(string holder, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            Holder = holder;
            Interlocked.Increment(ref Active);
            StateStarted.TrySetResult();
            try
            {
                yield return Initial;
                await foreach (var state in States.Reader.ReadAllAsync(token)) yield return state;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationChangesAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("This fixture does not subscribe to all information fields.");
        public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeNetworkEndpointsAsync(string id, TimeSpan interval,
            CancellationToken token, bool includeCurrent = true)
            => throw new InvalidOperationException("This consumer must not collect network endpoint information.");
        public IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeChangesAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("A single fetch must await sample completion, including an unchanged sample.");
        public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(string holder, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            Holder = holder;
            Interlocked.Increment(ref Active);
            AcpiStarted.TrySetResult();
            try
            {
                await foreach (var state in Acpi.Reader.ReadAllAsync(token)) yield return state;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
