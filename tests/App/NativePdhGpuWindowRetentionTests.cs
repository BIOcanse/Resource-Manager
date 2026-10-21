using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Native.Pdh;

namespace Resource_Manager_APP.Tests;
public sealed class NativePdhGpuWindowRetentionTests
{
    [Fact] public void StopAndOwnerDisposalDoNotCloseBorrowedOriginalPdhSession()
    {
        var native = new Native();
        using var source = new NativePdhSession(native, new(TimeSpan.Zero, TimeSpan.FromSeconds(10), 5000), new Clock());
        var inventory = new ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldSnapshot(null, null, [], [new(42, "100", [])]);
        var fields = ResourceManager.ReadWrite.ProcessFields.WindowsPdhProcessGpuFields.Project(source.Read(), inventory, _ => 100);
        var current = new ResourceManager.ReadWrite.ProcessFields.ProcessFieldCurrentState();
        current.Apply([ResourceManager.ReadWrite.ProcessFields.WindowsPdhProcessGpuFields.FieldName], fields);
        using var owner = new ResourceManager.ReadWrite.Windows.Processes.WindowsProcessFieldOwner(current);
        owner.BindGpuSource(source);
        using var borrow = owner.BorrowSubscriptionCapture();
        var field = owner.Backend.Catalog.Fields.Single().Field;
        borrow.SynchronizeDemand([field]); borrow.SynchronizeDemand([]); borrow.Dispose(); owner.Dispose();
        Assert.Equal(0, native.Destroys);
        source.Dispose(); source.Dispose(); Assert.Equal(1, native.Destroys);
    }
    [Fact] public void OriginalLastGoodRetainsExactOldWindowRatherThanStampingReadTime()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new NativePdhSession(native, new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), 5000), clock);
        var first = source.Read(); Assert.Single(first.Snapshot!.EngineWindows);
        native.Fail = true; clock.Now += TimeSpan.FromMilliseconds(100);
        var fallback = source.Read();
        Assert.Equal(NativePdhProviderAvailability.LastGood, fallback.Snapshot!.GpuEngineObservation.Availability);
        Assert.Equal(first.Snapshot.EngineWindows, fallback.Snapshot.EngineWindows);
        Assert.Equal(first.Snapshot.GpuEngineObservation.ObservedAt, fallback.Snapshot.GpuEngineObservation.ObservedAt);
        Assert.Equal(1, native.WindowCopies);
    }
    [Fact] public void MissingOptionalSidecarKeepsOriginalAdapterRowsButNoAttributionProof()
    {
        var native = new Native { NoWindows = true };
        using var source = new NativePdhSession(native, new(TimeSpan.Zero, TimeSpan.FromSeconds(10), 5000), new Clock());
        var read = source.Read(); Assert.Single(read.Snapshot!.EngineRows); Assert.Empty(read.Snapshot.EngineWindows);
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Native : INativePdhApi
    {
        internal bool Fail, NoWindows; internal int WindowCopies, Destroys;
        public uint GetAbiVersion() => NativeCoreAbi.Version;
        public NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle)
        { handle = new(1); return NativePdhResultCode.Ok; }
        public void Destroy(IntPtr handle) { Destroys++; }
        public NativePdhResultCode RequestTopologyRefresh(IntPtr handle) => NativePdhResultCode.Ok;
        public NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header)
        {
            header.AbiVersion = NativeCoreAbi.Version; header.StructSize = (uint)Marshal.SizeOf<NativePdhFrameHeader>();
            header.Sequence = 7; header.EngineRowCount = 1;
            header.Flags = (uint)(NativePdhFrameFlags.HasData | NativePdhFrameFlags.GpuEngineComplete);
            return Fail ? NativePdhResultCode.PdhError : NativePdhResultCode.Ok;
        }
        public NativePdhResultCode CopyFrame(IntPtr handle, ulong expectedSequence,
            NativePdhGpuEngineRow[] engineRows, NativePdhGpuMemoryRow[] memoryRows, out NativePdhSystemIo systemIo)
        {
            engineRows[0] = new() { AdapterLuid = 23, ProcessId = 42, UsagePercent = 25 };
            systemIo = new() { StructSize = (uint)Marshal.SizeOf<NativePdhSystemIo>() }; return NativePdhResultCode.Ok;
        }
        public NativePdhResultCode CopyGpuEngineWindows(IntPtr handle, ulong sequence, out NativePdhGpuEngineWindow[] windows)
        {
            WindowCopies++;
            windows = NoWindows ? [] : [new() { AdapterLuid = 23, ProcessId = 42, BeginFileTime = 150,
                EndFileTime = 190, BeginCounter = 10, EndCounter = 20, CounterFrequency = 100 }];
            return NoWindows ? NativePdhResultCode.Unavailable : NativePdhResultCode.Ok;
        }
        public NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics) => NativePdhResultCode.Ok;
    }
}
