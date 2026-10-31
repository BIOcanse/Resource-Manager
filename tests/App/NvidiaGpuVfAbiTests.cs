using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaGpuVfAbiTests
{
    // Independently calculated, source-derived fixture declarations (no binary captured from hardware).
    // LACT a844b05f2cf67b93292f049f9abf03d1e9acb885,
    // lact-daemon/src/server/gpu_controller/nvidia/nvapi.rs, MIT.
    // Legacy: falahati/NvAPIWrapper 2a41eb4d6b1bceff5dbb33a3b5ea70bd582214a7,
    // NvAPIWrapper/Native/GPU/Structures/Private{ClockBoostMasks,ClockBoostTable,VFPCurve}V1.cs,
    // LGPL-3.0: numerical parameters/layouts only; implementation not copied.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct InfoV1
    {
        public uint Version;
        public fixed uint Masks[8];
        public fixed byte Reserved[32];
        public fixed byte Points[255 * 24];
    }
    [StructLayout(LayoutKind.Explicit, Size = 88844)]
    private unsafe struct StatusV3
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public fixed uint Masks[8];
        [FieldOffset(36)] public byte BaseSupported;
        [FieldOffset(37)] public fixed byte Reserved[64];
        [FieldOffset(104)] public fixed byte Points[255 * 348];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct ControlV1
    {
        public uint Version;
        public fixed uint Masks[8];
        public fixed byte Reserved[32];
        public fixed byte Points[255 * 36];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct LegacyInfoV1
    {
        public uint Version;
        public fixed uint Masks[4];
        public fixed uint Unknown1[8];
        public fixed byte Points[103 * 24];
        public fixed uint Unknown2[916];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct LegacyStatusV1
    {
        public uint Version;
        public fixed uint Masks[4];
        public fixed uint Unknown1[12];
        public fixed byte GpuPoints[80 * 28];
        public fixed byte MemoryPoints[23 * 28];
        public fixed uint Unknown2[1064];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct LegacyControlV1
    {
        public uint Version;
        public fixed uint Masks[4];
        public fixed uint Unknown1[12];
        public fixed byte GpuPoints[80 * 36];
        public fixed uint MemoryFilled[23];
        public fixed int MemoryDeltas[23];
        public fixed uint Unknown2[1529];
    }

    [Fact]
    public void FixedSourceLayoutsKeepTheFullStatusAllocationAndOrVersion()
    {
        Assert.Equal(6188, Marshal.SizeOf<InfoV1>());
        Assert.Equal(6188, Marshal.SizeOf<LegacyInfoV1>());
        Assert.Equal(88844, Marshal.SizeOf<StatusV3>());
        Assert.Equal(7208, Marshal.SizeOf<LegacyStatusV1>());
        Assert.Equal(9248, Marshal.SizeOf<ControlV1>());
        Assert.Equal(9248, Marshal.SizeOf<LegacyControlV1>());
        Assert.Equal(104, Marshal.OffsetOf<StatusV3>(nameof(StatusV3.Points)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<LegacyStatusV1>(nameof(LegacyStatusV1.GpuPoints)).ToInt32());
        Assert.Equal(0x00035B0Cu, (uint)Marshal.SizeOf<StatusV3>() | (3u << 16));
        Assert.NotEqual(Marshal.SizeOf<StatusV3>(), (int)(0x00035B0Cu & 0xffff));
    }

    [Fact]
    public void ModernReadsInSourceOrderAndPreservesSparseNativeIdsAndUnits()
    {
        var fixture = Fixture(modern: true);
        var calls = new List<uint>();
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) =>
        {
            calls.Add(id);
            if (id == 0x21537AD4)
            {
                Assert.Equal(88844, bytes.Length);
                Assert.Equal(fixture.Info.AsSpan(4, 32).ToArray(), bytes.AsSpan(4, 32).ToArray());
            }
            return Copy(fixture, id, bytes);
        });
        Assert.Equal(new uint[] { 0x507B4B59, 0x21537AD4, 0x23F1B133, 0x6FF81213 }, calls);
        var snapshot = Assert.IsType<NvidiaGpuVfCurveSnapshot>(result.Snapshot);
        Assert.Equal(NvidiaGpuVfCurveAbis.Modern, snapshot.Abi);
        Assert.Equal(new[] { 1, 65, 254 }, snapshot.Points.Select(p => p.PointId));
        Assert.Equal(712345u, snapshot.Points[0].VoltageMicrovolts);
        Assert.Equal(1500123, snapshot.Points[0].DefaultFrequencyKhz);
        Assert.Equal(1520123, snapshot.Points[0].CurrentFrequencyKhz);
        Assert.Equal(20000, snapshot.Points[0].FrequencyOffsetKhz); // 15,000 table + 5,000 scalar, no old *2 compensation.
        Assert.Equal(-500000, snapshot.Points[0].MinimumOffsetKhz);
        Assert.False(snapshot.CanWrite);
        Assert.Equal(NvidiaGpuVfCurveAbis.LiveWriteUnavailable, snapshot.UnavailableReason);
    }

    [Fact]
    public void LegacyStatusIsRequestedOnlyAfterExplicitVersionRejection()
    {
        var fixture = Fixture(modern: false);
        var statusSizes = new List<int>();
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) =>
        {
            if (id == 0x21537AD4)
            {
                statusSizes.Add(bytes.Length);
                if (bytes.Length == 88844) return -9;
            }
            return Copy(fixture, id, bytes);
        });
        Assert.Equal(new[] { 88844, 7208 }, statusSizes);
        Assert.Equal(NvidiaGpuVfCurveAbis.Legacy, result.Snapshot!.Abi);
        Assert.Equal(new[] { 1, 65 }, result.Snapshot.Points.Select(p => p.PointId));
        Assert.Equal(1500123, result.Snapshot.Points[0].DefaultFrequencyKhz);
        Assert.False(result.Snapshot.CanWrite);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-104, true)]
    public void ArbitraryGetFailuresDoNotGuessLegacyLayout(int status, bool unsupported)
    {
        var fixture = Fixture(modern: true);
        var calls = new List<uint>();
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) =>
        {
            calls.Add(id);
            return id == 0x21537AD4 ? status : Copy(fixture, id, bytes);
        });
        Assert.Null(result.Snapshot);
        Assert.Equal(status, result.NativeStatus);
        Assert.Equal(unsupported, result.IsUnsupported);
        Assert.Equal(new uint[] { 0x507B4B59, 0x21537AD4 }, calls);
    }

    [Fact]
    public void UnknownReturnedVersionStopsWithoutLegacyOrScalarFallback()
    {
        var fixture = Fixture(modern: true);
        var calls = new List<uint>();
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) =>
        {
            calls.Add(id);
            var code = Copy(fixture, id, bytes);
            if (id == 0x21537AD4) Put(bytes, 0, 0x00045B0C);
            return code;
        });
        Assert.Null(result.Snapshot);
        Assert.False(result.IsUnsupported);
        Assert.Equal("nvidia-vf-unknown-status-version", result.UnavailableReason);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void MissingBaseTupleSubtractsBothKnownOffsetsAndNeverMakesAZeroCurve()
    {
        var fixture = Fixture(modern: true);
        fixture.Status[36] = 0;
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) => Copy(fixture, id, bytes));
        Assert.Equal(1500123, result.Snapshot!.Points[0].DefaultFrequencyKhz);
        Put(fixture.Status, 104 + 1 * 348 + 4, 0);
        result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) => Copy(fixture, id, bytes));
        Assert.Null(result.Snapshot);
        Assert.Equal("nvidia-vf-invalid-point-values", result.UnavailableReason);
    }

    [Fact]
    public void PointTypesAndVoltageBasedFlagFilterPointsWithoutRenumbering()
    {
        var fixture = Fixture(modern: true);
        fixture.Info[68 + 1 * 24 + 4] = 0;
        Put(fixture.Info, 68 + 65 * 24, 7);
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) => Copy(fixture, id, bytes));
        Assert.Equal(254, Assert.Single(result.Snapshot!.Points).PointId);
    }

    [Fact]
    public void TruncatedOrMismatchedMasksAreConcreteFailures()
    {
        var fixture = Fixture(modern: true);
        var malformed = NvidiaGpuVfCurveAbi.Decode("gpu:test", NvidiaGpuVfCurveAbis.Modern,
            fixture.Info, new byte[0x5B0C], fixture.Control, 5000, -500000, 500000);
        Assert.Equal("nvidia-vf-unrecognized-buffer-layout", malformed.UnavailableReason);
        Put(fixture.Control, 4, 0);
        var result = NvidiaNvapiControlBridge.ReadVfCurveBuffers("gpu:test", (id, bytes) => Copy(fixture, id, bytes));
        Assert.Equal("nvidia-vf-inconsistent-point-masks", result.UnavailableReason);
    }

    [Fact]
    public void ScalarFallbackUsesOnlyActualPstateOffsetAndRange()
    {
        var fixture = Fixture(modern: true);
        var result = NvidiaNvapiControlBridge.ReadScalarVfCurveBuffer("gpu:test", bytes =>
        {
            fixture.Pstate.CopyTo(bytes, 0);
            return 0;
        });
        Assert.Equal(NvidiaGpuVfCurveAbis.Scalar, result.Snapshot!.Abi);
        var point = Assert.Single(result.Snapshot.Points);
        Assert.Equal(5000, point.FrequencyOffsetKhz);
        Assert.Equal(-500000, point.MinimumOffsetKhz);
        Assert.Equal(500000, point.MaximumOffsetKhz);
        Assert.False(result.Snapshot.CanWrite);
        Put(fixture.Pstate, 44, 0);
        Put(fixture.Pstate, 48, 0);
        result = NvidiaNvapiControlBridge.ReadScalarVfCurveBuffer("gpu:test", bytes =>
        {
            fixture.Pstate.CopyTo(bytes, 0);
            return 0;
        });
        Assert.Null(result.Snapshot);
        Assert.Equal("nvidia-vf-scalar-range-unavailable", result.UnavailableReason);
    }

    private sealed record Tables(byte[] Info, byte[] Status, byte[] Control, byte[] Pstate);
    private static Tables Fixture(bool modern)
    {
        var info = new byte[6188];
        var status = new byte[modern ? 88844 : 7208];
        var control = new byte[9248];
        var pstate = new byte[7316];
        Put(info, 0, 0x0001182C);
        Put(status, 0, modern ? 0x00035B0Cu : 0x00011C28u);
        Put(control, 0, 0x00012420);
        if (modern) status[36] = 1;
        foreach (var id in modern ? new[] { 1, 65, 254 } : new[] { 1, 65 })
        {
            foreach (var buffer in new[] { info, status, control })
            {
                var maskOffset = 4 + id / 32 * 4;
                Put(buffer, maskOffset, U32(buffer, maskOffset) | (1u << (id % 32)));
            }
            if (modern) info[68 + id * 24 + 4] = 1;
            var offset = (modern ? 104 : 68) + id * (modern ? 348 : 28);
            Put(status, offset + 4, 1520123);
            Put(status, offset + 8, 712345);
            if (modern)
            {
                Put(status, offset + 12, 1500123);
                Put(status, offset + 16, 712345);
            }
            Put(control, 68 + id * 36 + 20, 15000);
        }
        Put(pstate, 0, 0x00011C94);
        Put(pstate, 8, 1);
        Put(pstate, 12, 1);
        Put(pstate, 40, 5000);
        Put(pstate, 44, unchecked((uint)-500000));
        Put(pstate, 48, 500000);
        return new(info, status, control, pstate);
    }
    private static int Copy(Tables fixture, uint id, byte[] target)
    {
        var source = id switch
        {
            0x507B4B59 => fixture.Info,
            0x21537AD4 => fixture.Status,
            0x23F1B133 => fixture.Control,
            0x6FF81213 => fixture.Pstate,
            _ => throw new InvalidOperationException("Unexpected native call.")
        };
        Assert.Equal(source.Length, target.Length);
        source.CopyTo(target, 0);
        return 0;
    }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
