using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativePhysicalNetworkConsumerTests
{
    [Fact]
    public void DistributedCollectorPublishesOnlyPhysicalLinkCapacityInExistingIoFrame()
    {
        var expectedBandwidth = ReadPhysicalLinkCapacity();
        var api = new NativePdhApi();
        Assert.Equal(NativePdhResultCode.Ok, api.Create(new NativePdhConfig
        {
            AbiVersion = NativeCoreAbi.Version,
            StructSize = 16,
            BaselineResetIntervalMilliseconds = 5_000
        }, out var handle));
        try
        {
            var header = NewHeader();
            Assert.Equal(NativePdhResultCode.NoData, api.Sample(handle, ref header));
            Thread.Sleep(100);
            header = NewHeader();
            Assert.Equal(NativePdhResultCode.Ok, api.Sample(handle, ref header));
            Assert.NotEqual(0U, header.Flags & (uint)NativePdhFrameFlags.NetworkComplete);
            Assert.Equal(NativePdhResultCode.Ok, api.CopyFrame(handle, header.Sequence,
                new NativePdhGpuEngineRow[header.EngineRowCount],
                new NativePdhGpuMemoryRow[header.MemoryRowCount], out var io));
            var networkMask = NativePdhIoValidMask.NetworkReceive | NativePdhIoValidMask.NetworkSend
                | NativePdhIoValidMask.NetworkBandwidth | NativePdhIoValidMask.NetworkUtilization;
            Assert.Equal(networkMask, io.ValidMask & networkMask);
            Assert.Equal(expectedBandwidth, ReadPhysicalLinkCapacity()); // Stable inventory during this read.
            Assert.Equal(expectedBandwidth, io.NetworkBandwidthBitsPerSecond);
            Assert.True(double.IsFinite(io.NetworkReceiveBytesPerSecond));
            Assert.True(double.IsFinite(io.NetworkSendBytesPerSecond));
            Assert.True(io.NetworkReceiveBytesPerSecond >= 0);
            Assert.True(io.NetworkSendBytesPerSecond >= 0);
            Assert.InRange(io.NetworkUtilizationPercent, 0, 100);
        }
        finally
        {
            api.Destroy(handle);
        }
    }

    private static NativePdhFrameHeader NewHeader() => new()
    {
        AbiVersion = NativeCoreAbi.Version,
        StructSize = 48
    };

    private static double ReadPhysicalLinkCapacity()
    {
        Assert.Equal(0U, GetIfTable2(out var table));
        Assert.NotEqual(IntPtr.Zero, table);
        try
        {
            var count = Marshal.ReadInt32(table);
            Assert.InRange(count, 0, 1024);
            double capacity = 0;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, 8 + index * 1352);
                var flags = Marshal.ReadByte(row, 1152);
                if ((flags & 0x83) != 1 || Marshal.ReadInt32(row, 1156) != 1)
                    continue;
                var transmit = unchecked((ulong)Marshal.ReadInt64(row, 1192));
                var receive = unchecked((ulong)Marshal.ReadInt64(row, 1200));
                capacity += Math.Max(transmit, receive);
            }
            return capacity;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr table);
}
