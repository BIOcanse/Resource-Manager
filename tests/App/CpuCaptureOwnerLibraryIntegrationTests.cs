using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class CpuCaptureOwnerLibraryIntegrationTests
{
    private static CpuOsTopologyInformation Facts() => new([new("core:0", 0, false, [0])], [])
        { LogicalProcessorBusyPercent = [25] };
    private static CpuTopologyCapture Capture() => new(
        CpuTopologyProviderTestFixture.CreateSnapshot(25), Facts(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task PairedCurrentAndCanonicalBackendPublishOnceWithoutReadSampling()
    {
        var reads = 0;
        var sample = Capture();
        using var owner = new CpuTopologyCaptureOwner(() => { reads++; return sample; }, () => { });
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(CpuTopologySnapshot).Assembly);
        using var signal = owner.SubscribePublications();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var notification = signal.WaitAsync(timeout.Token).AsTask();
        var backend = owner.CreateBackend(static _ => new(true, null, []));
        var catalog = await backend.DiscoverAsync(timeout.Token);
        await notification;
        Assert.Same(sample.Snapshot, owner.ReadState().Snapshot);
        Assert.Equal(sample.CapturedAt, owner.ReadInformation().ObservedAt);
        Assert.NotEmpty(catalog.Fields);
        foreach (var field in catalog.Fields) _ = backend.ReadCached(field.Field);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task FailureResetsThenClearsKnownValuesAndSignalsRecoverableCurrent()
    {
        var failure = false;
        var events = new List<string>();
        using var owner = new CpuTopologyCaptureOwner(() => failure ? throw new InvalidOperationException() : Capture(),
            () => events.Add("reset"), _ => events.Add("log"));
        await owner.CaptureAsync(CancellationToken.None);
        var before = owner.ReadInformation();
        failure = true;
        Assert.Equal(CpuTopologyCaptureCompletion.Failed, await owner.CaptureAsync(CancellationToken.None));
        Assert.Equal(new[] { "reset", "log" }, events);
        Assert.Null(owner.ReadState().Snapshot);
        Assert.Null(owner.ReadInformation().ObservedAt);
        Assert.Equal(before.Devices.Select(d => d.DeviceIdentity), owner.ReadInformation().Devices.Select(d => d.DeviceIdentity));
        Assert.All(owner.ReadInformation().Devices.SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        failure = false;
        Assert.Equal(CpuTopologyCaptureCompletion.Sampled, await owner.CaptureAsync(CancellationToken.None));
        Assert.NotNull(owner.ReadState().Snapshot);
    }

    [Fact]
    public async Task CancellationDuringSynchronousCaptureDoesNotCommitOrNotify()
    {
        using var canceled = new CancellationTokenSource();
        using var owner = new CpuTopologyCaptureOwner(() => { canceled.Cancel(); return Capture(); }, () => { });
        var before = owner.ReadState();
        using var signal = owner.SubscribePublications();
        var waiting = signal.WaitAsync(canceled.Token).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CaptureAsync(canceled.Token));
        Assert.Same(before, owner.ReadState());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task AtomicCallerRetirementRejectsCompletedCapture()
    {
        var gate = new object();
        var active = true;
        using var owner = new CpuTopologyCaptureOwner(() => { lock (gate) active = false; return Capture(); }, () => { });
        var before = owner.ReadState();
        var permission = new CpuTopologyCapturePermission(() => CpuTopologyCaptureAdmission.Capture,
            commit => { lock (gate) { if (!active) return false; commit(); return true; } });
        Assert.Equal(CpuTopologyCaptureCompletion.Skipped, await owner.CaptureAsync(CancellationToken.None, permission));
        Assert.Same(before, owner.ReadState());
    }

    [Fact]
    public async Task ResetRequestedDuringACaptureIsAppliedBeforeTheNextCaptureReads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var holdFirstRead = true;
        using var owner = new CpuTopologyCaptureOwner(() =>
        {
            events.Enqueue("read");
            if (Volatile.Read(ref holdFirstRead))
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return Capture();
        }, () => events.Enqueue("reset"));
        var running = Task.Run(() => owner.CaptureAsync(CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            owner.ResetUsageBaselineBeforeNextCapture();
            Assert.Equal(new[] { "read" }, events.ToArray());
        }
        finally { release.Set(); }
        Assert.Equal(CpuTopologyCaptureCompletion.Sampled, await running);
        Volatile.Write(ref holdFirstRead, false);
        await owner.CaptureAsync(CancellationToken.None);
        await owner.CaptureAsync(CancellationToken.None);
        Assert.Equal(new[] { "read", "reset", "read", "read" }, events.ToArray());
    }

    [Fact]
    public async Task BusyResetDefersAndDrainWaitsForTheOriginalGate()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var resets = 0;
        using var owner = new CpuTopologyCaptureOwner(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Capture();
        }, () => Interlocked.Increment(ref resets));
        var capturing = Task.Run(() => owner.CaptureAsync(CancellationToken.None));
        Task? drain = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            owner.ResetUsageBaselineBeforeNextCapture();
            Assert.Equal(0, resets);
            drain = owner.DrainAndResetUsageBaselineAsync();
            Assert.False(drain.IsCompleted);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CaptureAsync(canceled.Token));
        }
        finally { release.Set(); }
        await capturing;
        await drain!;
        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task AdmissionSkipAndResetDoesNotCaptureOrReplaceCurrent()
    {
        var reads = 0;
        var resets = 0;
        using var owner = new CpuTopologyCaptureOwner(() => { reads++; return Capture(); }, () => resets++);
        var before = owner.ReadState();
        var permission = new CpuTopologyCapturePermission(() => CpuTopologyCaptureAdmission.SkipAndResetBaseline,
            _ => throw new InvalidOperationException("Skip must not attempt publication."));
        Assert.Equal(CpuTopologyCaptureCompletion.Skipped, await owner.CaptureAsync(CancellationToken.None, permission));
        Assert.Equal(0, reads);
        Assert.Equal(1, resets);
        Assert.Same(before, owner.ReadState());
        owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.CaptureAsync(CancellationToken.None));
        Assert.Same(before, owner.ReadState());
        Assert.Equal(1, resets);
    }
}
