using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using static Resource_Manager_APP.Tests.KernelEtwFixtures.NativeKernelEtwTestResource;

namespace Resource_Manager_APP.Tests;

/// <summary>G4 draft: actual public diagnostic reads over the real native owner and scripted OS facts.</summary>
public sealed class KernelEtwThreadDiagnosticSamplingTests
{
    [Fact]
    public async Task AsyncReadWaitsForItsSampleTicketAndSameWindowReadsUseTheConfirmedFacts()
    {
        using var run = new CaptureRun();
        using var request = run.Register();
        var snapshot = run.CloseWindow();
        var before = run.Resource.DiagnosticUsage();
        for (var i = 0; i < 50; ++i)
        {
            run.Resource.Publish(5001 + i);
            Assert.Same(snapshot, run.Owner.Read());
            _ = run.Owner.ReadObservation();
        }
        Assert.Equal(before.NativeReads, run.Resource.DiagnosticUsage().NativeReads);
        run.Counters(20, 30, "late name");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = request.ReadWindowAsync(snapshot, timeout.Token);
        Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
        Assert.False(pending.IsCompleted); // An existing bank cannot satisfy the new sample ticket.
        Assert.Equal(before.NativeReads, run.Resource.DiagnosticUsage().NativeReads);
        run.Resource.Publish(5100);
        var first = Assert.IsType<CpuProcessThreadWindow>(await pending.WaitAsync(timeout.Token));
        AssertOldThreadFacts(run, first, 18, 27);
        var sampled = run.Resource.DiagnosticUsage();
        Assert.Equal(before.NativeReads + 1, sampled.NativeReads);
        run.Counters(200, 300, "replacement must not be read for the same window");
        for (var i = 0; i < 50; ++i) run.Resource.Publish(5101 + i);
        Assert.Same(first, request.ReadWindow(snapshot));
        Assert.Same(first, await request.ReadWindowAsync(snapshot, timeout.Token));
        Assert.Equal(0u, run.Resource.PendingDiagnosticSamples);
        Assert.Equal(sampled.NativeReads, run.Resource.DiagnosticUsage().NativeReads);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
        Assert.Empty(run.Resource.CallbackFailures);
    }

    [Fact]
    public async Task SynchronousPublicReadUsesTheSameExplicitSampleWithoutHoldingTheRegistryGate()
    {
        using var run = new CaptureRun();
        using var request = run.Register();
        var snapshot = run.CloseWindow();
        run.Counters(20, 30, "late name");
        var before = run.Resource.DiagnosticUsage();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Task.Run(() => request.ReadWindow(snapshot));
        await Until(() => run.Resource.PendingDiagnosticSamples == 1, timeout.Token);
        var registryGate = typeof(EtwCpuResidencyCaptureOwner).GetField("diagnosticGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run.Owner)!;
        Assert.True(Monitor.TryEnter(registryGate), "The synchronous wait must not hold the registry gate.");
        Monitor.Exit(registryGate);
        Assert.False(pending.IsCompleted);
        run.Resource.Publish(5100);
        var first = Assert.IsType<CpuProcessThreadWindow>(await pending.WaitAsync(timeout.Token));
        AssertOldThreadFacts(run, first, 18, 27);
        Assert.Equal(before.NativeReads + 1, run.Resource.DiagnosticUsage().NativeReads);
        Assert.Same(first, request.ReadWindow(snapshot));
        Assert.Equal(0u, run.Resource.PendingDiagnosticSamples);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
    }

    [Fact]
    public async Task CallerCancellationKeepsTheSubmittedTicketForRetryAndDoesNotControlTheResource()
    {
        using var run = new CaptureRun();
        using var request = run.Register();
        var snapshot = run.CloseWindow();
        run.Counters(20, 30, "late name");
        var before = run.Resource.DiagnosticUsage();
        using var cancellation = new CancellationTokenSource();
        var canceled = request.ReadWindowAsync(snapshot, cancellation.Token);
        Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Same(snapshot, run.Owner.Read());
        Assert.True(run.Owner.HasSessionLease);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retry = request.ReadWindowAsync(snapshot, timeout.Token);
        Assert.Equal(1u, run.Resource.PendingDiagnosticSamples); // Retry reuses the original ticket.
        run.Resource.Publish(5100);
        var result = Assert.IsType<CpuProcessThreadWindow>(await retry.WaitAsync(timeout.Token));
        AssertOldThreadFacts(run, result, 18, 27);
        Assert.Equal(before.NativeReads + 1, run.Resource.DiagnosticUsage().NativeReads);
    }

    [Fact]
    public async Task RemovingARegistrationDuringItsReadReleasesHandlesWithoutFailingOrStoppingTheResource()
    {
        using var run = new CaptureRun();
        var request = run.Register();
        try
        {
            var snapshot = run.CloseWindow();
            run.Counters(20, 30, "late name");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = request.ReadWindowAsync(snapshot, timeout.Token);
            Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
            request.Dispose();
            Assert.Null(await pending.WaitAsync(timeout.Token));
            run.Resource.Publish(5100); // Both staged sample/removal commands run on the original consumer.
            Assert.Equal(0UL, run.Resource.DiagnosticUsage().OpenHandles);
            Assert.Equal(0u, run.Resource.PendingDiagnosticSamples);
            Assert.True(run.Resource.GetSnapshot().IsRunning);
            Assert.True(run.Owner.HasSessionLease);
            Assert.Equal(1, run.Resource.Creations);
            Assert.Equal(0, run.Resource.Stops);
            Assert.Same(snapshot, run.Owner.Read());
            Assert.Empty(run.Resource.CallbackFailures);
            using var recovered = run.Owner.RequestThreadDiagnostics(CaptureRun.Pid, run.ProcessBirth);
            run.Resource.Publish(5200);
            var next = recovered.ReadWindowAsync(snapshot, timeout.Token);
            Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
            run.Resource.Publish(5300);
            Assert.NotNull(await next.WaitAsync(timeout.Token));
        }
        finally { request.Dispose(); }
    }

    [Fact]
    public async Task WrongWindowGenerationProcessOrBirthCannotStageDiagnosticSamples()
    {
        using var run = new CaptureRun();
        using var request = run.Register();
        var snapshot = run.CloseWindow();
        var reads = run.Resource.DiagnosticUsage().NativeReads;
        Assert.Null(request.ReadWindow(snapshot with { SessionGeneration = snapshot.SessionGeneration + 1 }));
        var process = Assert.Single(snapshot.Processes);
        Assert.Null(await request.ReadWindowAsync(snapshot with { Processes = [process with { ProcessId = CaptureRun.Pid + 1 }] }));
        Assert.Null(await request.ReadWindowAsync(snapshot with
        { Processes = [process with { ProcessStartKey = (run.ProcessBirth + 1).ToString(CultureInfo.InvariantCulture) }] }));
        Assert.Null(request.ReadWindow(snapshot with { MeasuredFrom = request.RequestedAt.AddTicks(-1) }));
        Assert.Equal(0u, run.Resource.PendingDiagnosticSamples);
        Assert.Equal(reads, run.Resource.DiagnosticUsage().NativeReads);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
    }

    [Fact]
    public async Task DeniedThreadOpenPublishesAnErrorForTheKnownCpuLifetimeWithoutInventingBirthOrCounters()
    {
        using var run = new CaptureRun(denyOpen: true);
        using var request = run.Register();
        var snapshot = run.CloseWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = request.ReadWindowAsync(snapshot, timeout.Token);
        Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
        run.Resource.Publish(5100);
        var result = Assert.IsType<CpuProcessThreadWindow>(await pending.WaitAsync(timeout.Token));
        var row = Assert.Single(result.Process.Threads);
        Assert.Equal(Assert.Single(Assert.Single(snapshot.Processes).Threads).ThreadInstanceId, row.ThreadInstanceId);
        var facts = Assert.IsType<CpuThreadDiagnosticFacts>(row.Diagnostic);
        Assert.Null(facts.CreationFileTime100Ns);
        Assert.Null(facts.Description);
        Assert.Null(facts.StartAddress);
        Assert.Null(facts.Counters);
        Assert.Equal(5, facts.NativeError);
        Assert.True(run.Resource.GetSnapshot().IsRunning);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
    }

    private static void AssertOldThreadFacts(CaptureRun run, CpuProcessThreadWindow window, long kernel, long user)
    {
        Assert.Equal(run.Window!.MeasuredFrom, window.MeasuredFrom);
        Assert.Equal(run.Window.MeasuredThrough, window.MeasuredThrough);
        Assert.Equal(run.Resource.Generation, window.SessionGeneration);
        var row = Assert.Single(window.Process.Threads);
        Assert.Equal(CaptureRun.Tid, row.ThreadId);
        var facts = Assert.IsType<CpuThreadDiagnosticFacts>(row.Diagnostic);
        Assert.Equal(run.ThreadBirth, facts.CreationFileTime100Ns);
        Assert.Equal("late name", facts.Description);
        Assert.Null(facts.DescriptionHResult);
        Assert.Equal(0x1000UL, facts.StartAddress);
        Assert.Equal(CpuThreadStartAddressSource.Win32StartAddress, facts.StartAddressSource);
        Assert.Equal(@"C:\fixture\worker.dll", facts.StartModulePath);
        Assert.Null(facts.NativeError);
        var counters = Assert.IsType<CpuThreadCounterDelta>(facts.Counters);
        Assert.Equal(run.Origin.AddSeconds(-1), counters.ReadFrom);
        Assert.Equal(run.Origin.AddSeconds(6), counters.ReadThrough);
        Assert.Equal(kernel, counters.KernelTime100Ns);
        Assert.Equal(user, counters.UserTime100Ns);
    }

    private static async Task Until(Func<bool> ready, CancellationToken cancellationToken)
    {
        while (!ready()) await Task.Delay(1, cancellationToken);
    }

    private sealed class CaptureRun : IDisposable
    {
        internal const int Pid = 41, Tid = 10;
        internal DateTimeOffset Origin { get; } = DateTimeOffset.UtcNow.AddMinutes(2);
        internal long ProcessBirth { get; }
        internal long ThreadBirth { get; }
        private readonly bool denyOpen;
        internal NativeKernelEtwTestResource Resource { get; }
        internal EtwCpuResidencyCaptureOwner Owner { get; }
        internal CpuCoreResidencySnapshot? Window { get; private set; }
        internal CaptureRun(bool denyOpen = false)
        {
            this.denyOpen = denyOpen;
            ProcessBirth = Origin.AddDays(-1).ToFileTime();
            ThreadBirth = Origin.AddHours(-1).ToFileTime();
            var config = NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000, maximumProcessors: 1,
                windowMilliseconds: 5000);
            if (denyOpen) config.Residency.MaximumThreads = 2;
            Resource = new(config, Origin, DiagnosticSourceMode.Scripted);
            CpuResidencyCaptureConfiguration plan = new(5000, 32,
                new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(),
                "scripted native diagnostic test", 50);
            Owner = new(Resource, () => plan);
            Owner.EnsureStarted();
            Assert.True(Owner.HasSessionLease);
            Assert.False(Owner.IsWindowActive);
            // Open the observation at this fixture's native due time, QPC zero.
            Owner.BeginWindow();
            Assert.True(Owner.IsWindowActive);
            Resource.ConfigureDiagnosticScript(Origin.AddSeconds(-1).ToFileTime(),
                [new(Pid, ProcessBirth)],
                [new(Pid, Tid, ThreadBirth, Kernel: 2, User: 3, DescriptionHResult: -1,
                    StartAddress: 0x1000, ModulePath: @"C:\fixture\worker.dll", CanOpen: !denyOpen)]);
            Resource.ProcessStart(0, Origin, Pid, 11, ProcessBirth, "scripted process");
            Resource.ThreadStart(0, Origin, Pid, Tid);
        }
        internal EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest Register()
        {
            var request = Owner.RequestThreadDiagnostics(Pid, ProcessBirth);
            Resource.Publish(0); // Registration and inventory baseline are acknowledged before counter changes.
            return request;
        }
        internal CpuCoreResidencySnapshot CloseWindow()
        {
            Resource.CSwitch(0, Origin, 0, 0, 0, Pid, Tid);
            Resource.CSwitch(5000, Origin.AddSeconds(5), 0, Pid, Tid, 0, 0);
            Resource.Publish(5000);
            Assert.True(Owner.Collect());
            Window = Assert.IsType<CpuCoreResidencySnapshot>(Owner.Read());
            Assert.Equal(Origin, Window.MeasuredFrom);
            Assert.Equal(Origin.AddSeconds(5), Window.MeasuredThrough);
            Assert.Equal(Pid, Assert.Single(Window.Processes).ProcessId);
            return Window;
        }
        internal void Counters(long kernel, long user, string? name)
            => Resource.ConfigureDiagnosticScript(Origin.AddSeconds(6).ToFileTime(), [new(Pid, ProcessBirth)],
                [new(Pid, Tid, ThreadBirth, Kernel: kernel, User: user, Description: name,
                    StartAddress: 0x1000, ModulePath: @"C:\fixture\worker.dll", CanOpen: !denyOpen)]);
        public void Dispose() { Owner.Dispose(); Resource.Dispose(); }
    }
}
