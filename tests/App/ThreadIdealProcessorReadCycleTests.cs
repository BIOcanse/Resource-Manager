using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class ThreadIdealProcessorReadCycleTests
{
    private const ulong Start = 133_900_000_000_000_000;

    [Fact]
    public void OneCycleReadsOneInventoryAndOneParentAndHandleForEachSelectedThread()
    {
        var fixture = new Fixture(3, 4);
        var cycle = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        foreach (var process in fixture.Inventory)
        {
            var result = cycle.ReadProcessThreads(process.Key, Start, 4);
            Assert.Equal(RecoveryReadStatus.Found, result.Status);
            Assert.Equal(4, result.Value!.Count);
            foreach (var thread in result.Value)
            {
                // Receipt observation shares the cycle's facts, not another parent or thread open.
                Assert.Same(thread, cycle.ReadThread(process.Key, Start, thread.ThreadId).Value);
                Assert.Equal(new ThreadIdealProcessor(0, 0), thread.IdealProcessor);
                Assert.Equal(15UL, thread.AllowedMask);
                Assert.Equal(new uint[] { 3, 7 }, thread.SelectedCpuSetIds);
            }
        }
        Assert.Equal(1, fixture.Snapshots);
        Assert.Equal(3, fixture.ParentReads);
        Assert.Equal(12, fixture.Api.Opened);
        Assert.Equal(12, fixture.Api.Closed);
        Assert.Equal(12, fixture.Api.OwnerReads);
        Assert.Equal(12, fixture.Api.TimeReads);
        Assert.Equal(12, fixture.Api.IdealReads);
        Assert.Equal(12, fixture.Api.AffinityReads);
        Assert.Equal(12, fixture.Api.CpuSetReads);
        Assert.Empty(fixture.Api.Handles);

        var next = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        Assert.Equal(RecoveryReadStatus.Found, next.ReadProcessThreads(100, Start, 4).Status);
        Assert.Equal(2, fixture.Snapshots);
        Assert.Equal(4, fixture.ParentReads);
        Assert.Equal(16, fixture.Api.Opened); // No fact cache crosses cycles.
    }

    [Fact]
    public void ParentReuseIsRejectedBeforeOpeningThreadsAndOnlyThatProcessIsAffected()
    {
        var fixture = new Fixture(2, 2);
        fixture.ParentStarts[100] = Start + 1;
        var cycle = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, cycle.ReadProcessThreads(100, Start, 2).Status);
        Assert.Equal(RecoveryReadStatus.Found, cycle.ReadProcessThreads(101, Start, 2).Status);
        Assert.Equal(1, fixture.Snapshots);
        Assert.Equal(2, fixture.ParentReads);
        Assert.Equal(2, fixture.Api.Opened);
    }

    [Fact]
    public void OldThreadCreationExitAndOpenFailureOmitOnlyThoseThreads()
    {
        var fixture = new Fixture(1, 5);
        fixture.Api.Creation[1000] = Start - 1;
        fixture.Api.Exit[1001] = Start + 10;
        fixture.Api.Missing.Add(1002); // Exits after inventory, before OpenThread.
        fixture.Api.NoCpuSets.Add(1003);
        var cycle = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        var result = cycle.ReadProcessThreads(100, Start, 5);
        Assert.Equal(RecoveryReadStatus.Found, result.Status);
        Assert.Equal(1004, Assert.Single(result.Value!).ThreadId);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, cycle.ReadThread(100, Start, 1000).Status);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, cycle.ReadThread(100, Start, 1001).Status);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, cycle.ReadThread(100, Start, 1002).Status);
        Assert.Equal(RecoveryReadStatus.Unavailable, cycle.ReadThread(100, Start, 1003).Status);
        Assert.Equal(1, fixture.ParentReads);
        Assert.Equal(5, fixture.Api.Opened);
        Assert.Equal(4, fixture.Api.Closed);
        Assert.Empty(fixture.Api.Handles);
    }

    [Fact]
    public void PerProcessThreadCapacityDoesNotRejectOtherProcesses()
    {
        var fixture = new Fixture(2, 3);
        var cycle = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        var over = cycle.ReadProcessThreads(100, Start, 2);
        Assert.Equal(RecoveryReadStatus.Unavailable, over.Status);
        Assert.Equal(122, over.NativeErrorCode);
        Assert.Equal(RecoveryReadStatus.Found, cycle.ReadProcessThreads(101, Start, 3).Status);
        Assert.Equal(1, fixture.Snapshots);
        Assert.Equal(3, fixture.Api.Opened);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("creation")]
    [InlineData("ideal")]
    [InlineData("affinity")]
    [InlineData("sets")]
    [InlineData("owner")]
    [InlineData("exit")]
    public void WriteUsesFreshParentAndHandleAndRefusesAChangedExpectedValue(string changed)
    {
        var fixture = new Fixture(1, 1);
        var expected = fixture.Writer.CreateThreadIdealProcessorReadCycle().ReadThread(100, Start, 1000).Value!;
        switch (changed)
        {
            case "parent": fixture.ParentStarts[100]++; break;
            case "creation": fixture.Api.Creation[1000]++; break;
            case "ideal": fixture.Api.Ideal = new(0, 2); break;
            case "affinity": fixture.Api.Mask = 7; break;
            case "sets": fixture.Api.Sets = [4]; break;
            case "owner": fixture.Api.Owners[1000] = 101; break;
            case "exit": fixture.Api.Exit[1000] = Start + 10; break;
        }
        Assert.False(fixture.Writer.TrySetThreadIdealProcessor(expected, new(0, 1)).Succeeded);
        Assert.Equal(2, fixture.Api.Opened);
        Assert.Equal(2, fixture.Api.Closed);
        Assert.Equal(changed == "owner" ? 1 : 2, fixture.ParentReads);
        Assert.Equal(0, fixture.Api.Writes);
        Assert.Empty(fixture.Api.Handles);
    }

    [Fact]
    public void UnchangedWriteComparesFromOneFreshHandleAndIndependentReadsStayFresh()
    {
        var fixture = new Fixture(1, 1);
        var cycle = fixture.Writer.CreateThreadIdealProcessorReadCycle();
        var expected = cycle.ReadThread(100, Start, 1000).Value!;
        Assert.True(fixture.Writer.TrySetThreadIdealProcessor(expected, new(0, 1)).Succeeded);
        Assert.Equal(1, fixture.Api.Writes);
        Assert.Equal(new ThreadIdealProcessor(0, 1), fixture.Writer.ReadThreadIdealProcessor(100, Start, 1000).Value!.IdealProcessor);
        Assert.Equal(3, fixture.ParentReads);
        Assert.Equal(3, fixture.Api.Opened);
        Assert.Equal(3, fixture.Api.Closed);
        Assert.Equal(expected, cycle.ReadThread(100, Start, 1000).Value); // Planning snapshot is immutable.
    }

    private sealed class Fixture
    {
        public readonly Dictionary<int, IReadOnlyList<int>> Inventory = [];
        public readonly Dictionary<int, ulong> ParentStarts = [];
        public readonly Api Api = new();
        public int ParentReads;
        public int Snapshots;
        public WindowsProcessResourcePolicyWriter Writer { get; }
        public Fixture(int processes, int threads)
        {
            for (var p = 0; p < processes; p++)
            {
                var pid = 100 + p;
                var ids = Enumerable.Range(1000 + p * threads, threads).ToArray();
                Inventory.Add(pid, ids);
                ParentStarts.Add(pid, Start);
                foreach (var tid in ids) { Api.Owners.Add(tid, pid); Api.Creation.Add(tid, Start + 1); }
            }
            Writer = new(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256), Api,
                pid => { ParentReads++; return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, DateTimeOffset.FromFileTime((long)ParentStarts[pid]))); },
                () => { Snapshots++; return RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>.Found(Inventory); });
        }
    }

    private sealed class Api : IThreadIdealProcessorNativeApi
    {
        public readonly Dictionary<IntPtr, int> Handles = [];
        public readonly Dictionary<int, int> Owners = [];
        public readonly Dictionary<int, ulong> Creation = [];
        public readonly Dictionary<int, ulong> Exit = [];
        public readonly HashSet<int> Missing = [];
        public readonly HashSet<int> NoCpuSets = [];
        public int Opened, Closed, OwnerReads, TimeReads, IdealReads, AffinityReads, CpuSetReads, Writes;
        public ThreadIdealProcessor Ideal = new(0, 0);
        public ulong Mask = 15;
        public IReadOnlyList<uint> Sets = [3, 7];
        public int LastError => 87;
        public bool UsesCpuSets => true;
        public IntPtr OpenThread(int threadId, bool write)
        {
            var handle = new IntPtr(++Opened);
            if (Missing.Contains(threadId)) return IntPtr.Zero;
            Handles.Add(handle, threadId);
            return handle;
        }
        public void CloseThread(IntPtr thread) { Assert.True(Handles.Remove(thread)); Closed++; }
        public uint ReadProcessId(IntPtr thread) { OwnerReads++; return (uint)Owners[Handles[thread]]; }
        public bool TryReadTimes(IntPtr thread, out ulong creation, out ulong exit)
        { TimeReads++; creation = Creation[Handles[thread]]; exit = Exit.GetValueOrDefault(Handles[thread]); return true; }
        public bool TryReadIdeal(IntPtr thread, out ThreadIdealProcessor ideal)
        { IdealReads++; Assert.True(Handles.ContainsKey(thread)); ideal = Ideal; return true; }
        public bool TryReadAffinity(IntPtr thread, out ushort group, out ulong mask)
        { AffinityReads++; Assert.True(Handles.ContainsKey(thread)); group = 0; mask = Mask; return true; }
        public IReadOnlyList<uint>? ReadSelectedCpuSets(IntPtr thread)
        { CpuSetReads++; return NoCpuSets.Contains(Handles[thread]) ? null : Sets; }
        public bool TrySetIdeal(IntPtr thread, ThreadIdealProcessor desired)
        { Writes++; Assert.True(Handles.ContainsKey(thread)); Ideal = desired; return true; }
    }
}
