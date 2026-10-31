using System.Collections.Frozen;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using static Resource_Manager_APP.Tests.KernelEtwFixtures.NativeKernelEtwTestResource;

namespace Resource_Manager_APP.Tests;

/// <summary>Same scripted OS primitives, retained old C# algorithm versus the actual public native-backed request.</summary>
public sealed class KernelEtwThreadDiagnosticEquivalenceTests
{
    [Theory]
    [InlineData("cumulative-and-name-retry")]
    [InlineData("newborn-zero-baseline")]
    [InlineData("exit-before-window")]
    [InlineData("failure-after-success")]
    [InlineData("failed-open-retry")]
    public async Task ScriptedOsFactsMatchEveryFieldOfTheRetainedCSharpDiagnosticRequest(string scenario)
    {
        using var run = new NativeRun(denyOpen: scenario == "failed-open-retry");
        using var native = run.Register();
        var scripted = new ScriptedOracleThreadDiagnosticSource();
        scripted.Configure(run.BaselineTime.ToFileTime(), run.ProcessRows(), run.ThreadRows(2, 3, nameError: -1));
        // Align request freshness before binding inventory; then supply the exact same explicit OS read clock.
        scripted.SetClock(native.RequestedAt.ToFileTime());
        using var oracleOwner = new OracleCpuThreadDiagnosticOwner(scripted);
        using var oracle = oracleOwner.RequestThreadDiagnostics(NativeRun.Pid, run.ProcessBirth);
        scripted.SetClock(run.BaselineTime.ToFileTime());
        oracleOwner.BindDiagnosticSource(new OracleKernelEtwEventSource(), run.Resource.Generation, static () => true);

        var newborn = scenario == "newborn-zero-baseline";
        var exited = scenario == "exit-before-window" ? run.Origin.AddTicks(-1).ToFileTime() : 0;
        run.Resource.CSwitch(0, run.Origin, 0, 0, 0, NativeRun.Pid, NativeRun.Tid);
        if (newborn)
        {
            var rows = run.ThreadRows(2, 3, nameError: -1, newborn: true, newbornKernel: 5, newbornUser: 7);
            run.ConfigureBoth(scripted, run.Origin.AddSeconds(2), rows);
            run.Resource.ThreadStart(2000, run.Origin.AddSeconds(2), NativeRun.Pid, NativeRun.NewTid);
            oracle.ObserveThreadStart(NativeRun.NewTid); // Exact target of the old ETW lifecycle callback.
            run.Resource.CSwitch(2000, run.Origin.AddSeconds(2), 0, NativeRun.Pid, NativeRun.Tid, NativeRun.Pid, NativeRun.NewTid);
        }
        run.Resource.CSwitch(5000, run.Origin.AddSeconds(5), 0, NativeRun.Pid,
            newborn ? NativeRun.NewTid : NativeRun.Tid, 0, 0);
        var snapshot = run.Commit(5000);
        var firstRows = run.ThreadRows(20, 30, name: "late name", exit: exited, newborn: newborn,
            newbornKernel: 20, newbornUser: 30);
        run.ConfigureBoth(scripted, run.Origin.AddSeconds(6), firstRows);
        var first = await ReadAndCompare(run, native, oracle, snapshot, 5001);
        if (scenario == "exit-before-window")
            Assert.Null(Assert.Single(first.Process.Threads).Diagnostic);
        else if (scenario == "failed-open-retry")
        {
            var denied = Assert.Single(first.Process.Threads).Diagnostic!;
            Assert.Null(denied.CreationFileTime100Ns);
            Assert.Null(denied.Counters);
            Assert.Equal(5, denied.NativeError);
            // A two-entry bound is deliberately smaller than the number of explicit retries.
            for (var round = 1; round < 10; ++round)
            {
                var start = round * 5000L; var end = start + 5000;
                run.Resource.CSwitch(start, run.Origin.AddMilliseconds(start), 0, 0, 0, NativeRun.Pid, NativeRun.Tid);
                run.Resource.CSwitch(end, run.Origin.AddMilliseconds(end), 0, NativeRun.Pid, NativeRun.Tid, 0, 0);
                var nextWindow = run.Commit(end);
                run.ConfigureBoth(scripted, run.Origin.AddMilliseconds(end + 1000), run.ThreadRows(20, 30, name: "late name"));
                _ = await ReadAndCompare(run, native, oracle, nextWindow, end + 1);
            }
            run.Resource.CSwitch(50000, run.Origin.AddSeconds(50), 0, 0, 0, NativeRun.Pid, NativeRun.Tid);
            run.Resource.CSwitch(55000, run.Origin.AddSeconds(55), 0, NativeRun.Pid, NativeRun.Tid, 0, 0);
            var recoveredWindow = run.Commit(55000);
            run.ConfigureBoth(scripted, run.Origin.AddSeconds(56), run.ThreadRows(20, 30, name: "recovered", canOpen: true));
            var recovered = await ReadAndCompare(run, native, oracle, recoveredWindow, 55001);
            var baseline = Assert.Single(recovered.Process.Threads).Diagnostic!;
            Assert.Equal(0, baseline.Counters!.KernelTime100Ns);
            Assert.Equal(0, baseline.Counters.UserTime100Ns);
            Assert.Equal(run.Origin.AddSeconds(56), baseline.Counters.ReadFrom);
            Assert.Null(baseline.NativeError);
            run.Resource.CSwitch(55000, run.Origin.AddSeconds(55), 0, 0, 0, NativeRun.Pid, NativeRun.Tid);
            run.Resource.CSwitch(60000, run.Origin.AddSeconds(60), 0, NativeRun.Pid, NativeRun.Tid, 0, 0);
            var laterWindow = run.Commit(60000);
            run.ConfigureBoth(scripted, run.Origin.AddSeconds(61), run.ThreadRows(25, 35, name: "changed", canOpen: true));
            var later = await ReadAndCompare(run, native, oracle, laterWindow, 60001);
            var delta = Assert.Single(later.Process.Threads).Diagnostic!;
            Assert.Equal(5, delta.Counters!.KernelTime100Ns);
            Assert.Equal(5, delta.Counters.UserTime100Ns);
            Assert.Equal(baseline.Counters.ReadFrom, delta.Counters.ReadFrom);
            Assert.Equal("recovered", delta.Description);
        }
        else
        {
            var old = Assert.Single(first.Process.Threads, row => row.ThreadId == NativeRun.Tid).Diagnostic!;
            Assert.Equal(18, old.Counters!.KernelTime100Ns);
            Assert.Equal(27, old.Counters.UserTime100Ns);
            Assert.Null(old.DescriptionHResult);
            if (newborn)
            {
                var born = Assert.Single(first.Process.Threads, row => row.ThreadId == NativeRun.NewTid).Diagnostic!;
                Assert.Equal(20, born.Counters!.KernelTime100Ns);
                Assert.Equal(30, born.Counters.UserTime100Ns);
                Assert.Equal(DateTimeOffset.FromFileTime(run.NewbornBirth), born.Counters.ReadFrom);
            }
        }

        if (scenario is "cumulative-and-name-retry" or "failure-after-success")
        {
            run.Resource.CSwitch(5000, run.Origin.AddSeconds(5), 0, 0, 0, NativeRun.Pid, NativeRun.Tid);
            run.Resource.CSwitch(10000, run.Origin.AddSeconds(10), 0, NativeRun.Pid, NativeRun.Tid, 0, 0);
            var secondSnapshot = run.Commit(10000);
            run.ConfigureBoth(scripted, run.Origin.AddSeconds(11),
                run.ThreadRows(25, 35, name: "changed name", canRead: scenario != "failure-after-success"));
            var second = await ReadAndCompare(run, native, oracle, secondSnapshot, 10001);
            var fact = Assert.Single(second.Process.Threads).Diagnostic!;
            Assert.Equal("late name", fact.Description); // Neither algorithm retries a successful description.
            if (scenario == "failure-after-success")
            {
                Assert.Null(fact.Counters);
                Assert.Equal(5, fact.NativeError);
            }
            else
            {
                Assert.Equal(23, fact.Counters!.KernelTime100Ns);
                Assert.Equal(32, fact.Counters.UserTime100Ns);
                Assert.Equal(run.BaselineTime, fact.Counters.ReadFrom);
            }
        }
        oracle.Dispose();
        Assert.Equal(0, scripted.OpenHandles);
        native.Dispose();
        run.Resource.Publish(Math.Max(run.Resource.Tick, 11000));
        Assert.Equal(0UL, run.Resource.DiagnosticUsage().OpenHandles);
        Assert.Equal(1, run.Resource.Creations);
        Assert.Equal(0, run.Resource.Stops);
        Assert.Empty(run.Resource.CallbackFailures);
    }

    private static async Task<CpuProcessThreadWindow> ReadAndCompare(NativeRun run,
        EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest native,
        OracleCpuThreadDiagnosticOwner.ThreadDiagnosticRequest oracle,
        CpuCoreResidencySnapshot snapshot, long tick)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = native.ReadWindowAsync(snapshot, timeout.Token);
        Assert.Equal(1u, run.Resource.PendingDiagnosticSamples);
        Assert.False(pending.IsCompleted);
        run.Resource.Publish(tick);
        var actual = Assert.IsType<CpuProcessThreadWindow>(await pending.WaitAsync(timeout.Token));
        var expected = Assert.IsType<CpuProcessThreadWindow>(oracle.ReadWindow(snapshot));
        Assert.Equal(expected.MeasuredFrom, actual.MeasuredFrom);
        Assert.Equal(expected.MeasuredThrough, actual.MeasuredThrough);
        Assert.Equal(expected.SessionGeneration, actual.SessionGeneration);
        Assert.Equal(expected.Process.ProcessInstanceId, actual.Process.ProcessInstanceId);
        Assert.Equal(expected.Process.ProcessStartKey, actual.Process.ProcessStartKey);
        Assert.Equal(expected.Process.Threads.OrderBy(row => row.ThreadInstanceId).Select(row => (row.ThreadInstanceId, row.ThreadId, row.Diagnostic)).ToArray(),
            actual.Process.Threads.OrderBy(row => row.ThreadInstanceId).Select(row => (row.ThreadInstanceId, row.ThreadId, row.Diagnostic)).ToArray());
        return actual;
    }

    private sealed class NativeRun : IDisposable
    {
        internal const int Pid = 41, Tid = 10, NewTid = 11;
        internal DateTimeOffset Origin { get; } = DateTimeOffset.UtcNow.AddMinutes(2);
        internal DateTimeOffset BaselineTime => Origin.AddSeconds(-1);
        internal long ProcessBirth { get; }
        internal long ThreadBirth { get; }
        internal long NewbornBirth => Origin.AddSeconds(2).ToFileTime();
        private readonly bool denyOpen;
        internal NativeKernelEtwTestResource Resource { get; }
        internal EtwCpuResidencyCaptureOwner Owner { get; }
        internal NativeRun(bool denyOpen = false)
        {
            this.denyOpen = denyOpen;
            ProcessBirth = Origin.AddDays(-1).ToFileTime();
            ThreadBirth = Origin.AddHours(-1).ToFileTime();
            var config = NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000,
                maximumProcessors: 1, windowMilliseconds: 5000);
            if (denyOpen) config.Residency.MaximumThreads = 2;
            Resource = new(config, Origin, DiagnosticSourceMode.Scripted);
            CpuResidencyCaptureConfiguration plan = new(5000, 32,
                new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(),
                "scripted native diagnostic equivalence");
            Owner = new(Resource, () => plan);
            Owner.EnsureStarted();
            Assert.False(Owner.IsWindowActive);
            // Both diagnostic owners consume the observation due at QPC zero.
            Owner.BeginWindow(TimeSpan.FromSeconds(1));
            Assert.True(Owner.IsWindowActive);
            Resource.ConfigureDiagnosticScript(BaselineTime.ToFileTime(), ProcessRows(), ThreadRows(2, 3, nameError: -1));
            Resource.ProcessStart(0, Origin, Pid, 11, ProcessBirth, "scripted process");
            Resource.ThreadStart(0, Origin, Pid, Tid);
        }
        internal DiagnosticProcessInput[] ProcessRows() => [new(Pid, ProcessBirth)];
        internal DiagnosticThreadInput[] ThreadRows(long kernel, long user, string? name = null, int? nameError = null,
            long exit = 0, bool canRead = true, bool newborn = false, long newbornKernel = 0, long newbornUser = 0, bool? canOpen = null)
            => [
                new(Pid, Tid, ThreadBirth, Kernel: kernel, User: user, Exit: exit, Description: name,
                    DescriptionHResult: nameError, StartAddress: 0x1000, ModulePath: @"C:\fixture\worker.dll",
                    CanOpen: canOpen ?? !denyOpen, CanReadTimes: canRead, InInventory: exit == 0),
                // Its module is already loaded, but the thread appears in inventory only at its lifecycle event.
                new(Pid, NewTid, NewbornBirth, Kernel: newbornKernel, User: newbornUser, Description: "newborn worker",
                    StartAddress: 0x2000, ModulePath: @"C:\fixture\worker.dll", InInventory: newborn)
            ];
        internal void ConfigureBoth(ScriptedOracleThreadDiagnosticSource oracle, DateTimeOffset now, DiagnosticThreadInput[] rows)
        {
            var process = ProcessRows();
            Resource.ConfigureDiagnosticScript(now.ToFileTime(), process, rows);
            oracle.Configure(now.ToFileTime(), process, rows);
        }
        internal EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest Register()
        {
            var request = Owner.RequestThreadDiagnostics(Pid, ProcessBirth);
            Resource.Publish(0);
            return request;
        }
        internal CpuCoreResidencySnapshot Commit(long tick)
        {
            Resource.Publish(tick);
            Assert.True(Owner.Collect());
            var snapshot = Assert.IsType<CpuCoreResidencySnapshot>(Owner.Read());
            Assert.Equal(Origin.AddMilliseconds(tick - 5000), snapshot.MeasuredFrom);
            Assert.Equal(Origin.AddMilliseconds(tick), snapshot.MeasuredThrough);
            return snapshot;
        }
        public void Dispose() { Owner.Dispose(); Resource.Dispose(); }
    }
}
