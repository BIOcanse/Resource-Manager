using System.Runtime.InteropServices;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Fact]
    public void CaptureUsesOneDisposableThreadAndExplicitOsAddressesWithoutModifyingCaller()
    {
        var caller = Environment.CurrentManagedThreadId;
        var access = new Access();
        var results = WindowsCpuThreadCpuidReader.Read([135, 1, 135], access, new List<string>());
        Assert.Equal(new[] { 1, 135 }, results.Select(static value => value.LogicalProcessorId));
        Assert.Equal(new[] { new CpuProcessorAddress(0, 1), new CpuProcessorAddress(2, 7) }, access.Selected);
        Assert.All(results, value => Assert.NotNull(value.Data));
        Assert.NotEqual(caller, access.Worker!.ManagedThreadId);
        Assert.False(access.Worker.IsAlive);
        Assert.Equal(caller, Environment.CurrentManagedThreadId);
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsCpuThreadCpuidReader.Read([-1], access, new List<string>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsCpuThreadCpuidReader.Read([4194304], access, new List<string>()));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("read")]
    public void FailedProcessorIsEmptyAndOtherProcessorsRemainReadable(string failure)
    {
        var access = new Access { Failure = failure };
        var results = WindowsCpuThreadCpuidReader.Read([1, 135], access, new List<string>());
        Assert.Null(results[0].Data); Assert.NotNull(results[1].Data);
        Assert.False(access.Worker!.IsAlive);
        if (failure is "select" or "before") Assert.DoesNotContain(1, access.ReadIds);
    }

    [Fact]
    public void UnexpectedWorkerFailureIsCaughtAndDoesNotPublishAPrefixOrEscapeTheThread()
    {
        var access = new Access { UnexpectedFailure = true };
        var notes = new List<string>();
        var results = WindowsCpuThreadCpuidReader.Read([1, 135], access, notes);
        Assert.All(results, value => Assert.Null(value.Data));
        Assert.Single(notes);
        Assert.False(access.Worker!.IsAlive);
    }

    [Fact]
    public void EmptyDemandDoesNotStartAnAccessThread()
    {
        var access = new Access();
        Assert.Empty(WindowsCpuThreadCpuidReader.Read([], access, new List<string>()));
        Assert.Null(access.Worker); Assert.Empty(access.Selected);
    }

    [Fact]
    public void ProductionCpuidMapsRealOsAddressesAndLeavesCallerGroupAffinityUntouched()
    {
        Assert.Equal(16, Marshal.SizeOf<TestGroupAffinity>());
        Assert.True(GetThreadGroupAffinity(GetCurrentThread(), out var before));
        var ids = WindowsCpuOsTopologyReader.ReadNumaNodes()!.SelectMany(static node => node.LogicalProcessorIds).Distinct().ToArray();
        var notes = new List<string>();
        var results = WindowsCpuThreadCpuidReader.Read(ids, notes);
        Assert.Empty(notes);
        Assert.Equal(ids.Order(), results.Select(static value => value.LogicalProcessorId));
        Assert.All(results, value => { Assert.NotNull(value.Data); Assert.NotEmpty(value.Data.Identity.Vendor); Assert.NotNull(value.Data.Signature); });
        Assert.True(GetThreadGroupAffinity(GetCurrentThread(), out var after));
        Assert.Equal(before.Mask, after.Mask); Assert.Equal(before.Group, after.Group);
        Assert.NotNull(WindowsCpuOsFeatureReader.Read());
        Assert.NotNull(WindowsCpuOsFeatureReader.ReadEnabledStateMask());
    }

    private sealed class Access : ICpuThreadCpuidAccess
    {
        private CpuProcessorAddress selected = new(0, 0);
        private bool hasRead;
        internal string? Failure { get; init; }
        internal bool UnexpectedFailure { get; init; }
        internal Thread? Worker { get; private set; }
        internal List<CpuProcessorAddress> Selected { get; } = [];
        internal List<int> ReadIds { get; } = [];
        public bool Select(CpuProcessorAddress address)
        {
            Worker ??= Thread.CurrentThread;
            Assert.Same(Worker, Thread.CurrentThread);
            selected = address; hasRead = false; Selected.Add(address);
            return !(address.ProcessorGroup == 0 && Failure == "select");
        }
        public CpuProcessorAddress Current()
            => selected.ProcessorGroup == 0 && ((Failure == "before" && !hasRead) || (Failure == "after" && hasRead))
                ? selected with { GroupRelativeIndex = 2 } : selected;
        public (int, int, int, int) Read(int leaf, int subleaf)
        {
            var id = selected.ProcessorGroup * 64 + selected.GroupRelativeIndex;
            hasRead = true; ReadIds.Add(id);
            if (Failure == "read" && id == 1) throw new InvalidOperationException("Processor unavailable.");
            if (UnexpectedFailure && id == 135) throw new IOException("Unexpected capture error.");
            return Registers("GenuineIntel").Read(leaf, subleaf);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TestGroupAffinity { public ulong Mask; public ushort Group, Reserved0, Reserved1, Reserved2; }
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(nint thread, out TestGroupAffinity affinity);
}
