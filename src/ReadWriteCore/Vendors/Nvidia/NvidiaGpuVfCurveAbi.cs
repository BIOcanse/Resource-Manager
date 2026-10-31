using System.Buffers.Binary;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

// ABI facts independently expressed from:
// ilya-zlobintsev/LACT a844b05f2cf67b93292f049f9abf03d1e9acb885,
// lact-daemon/src/server/gpu_controller/nvidia/nvapi.rs (MIT, Copyright 2023 Ilya Zlobintsev).
// Legacy numerical layouts/units only (no implementation copied): falahati/NvAPIWrapper
// 2a41eb4d6b1bceff5dbb33a3b5ea70bd582214a7, Native/GPU/Structures/
// Private{ClockBoostMasks,ClockBoostTable,VFPCurve}V1.cs (LGPL-3.0; numerical/layout facts).
internal static class NvidiaGpuVfCurveAbi
{
    internal const uint GetInfoId = 0x507B4B59;
    internal const uint GetStatusId = 0x21537AD4;
    internal const uint GetControlId = 0x23F1B133;
    // Status numbers: NVIDIA/nvapi a08f45f737bb142b070f1e23c58466ecbc82e398,
    // nvapi_lite_common.h NvAPI_Status (MIT; numerical facts only).
    internal const int IncompatibleVersion = -9;
    internal const int NotSupported = -104;
    internal const int InfoSize = 6188;
    internal const int ControlSize = 9248;
    internal const int ModernStatusSize = 88844; // 104 + 255 * 348; allocate full size, NOT version & 0xffff.
    internal const int LegacyStatusSize = 7208;
    internal const uint InfoVersion = 0x0001182C;
    internal const uint ControlVersion = 0x00012420;
    internal const uint ModernStatusVersion = 0x00035B0C; // sizeof | (3 << 16), including size's high bit.
    internal const uint LegacyStatusVersion = 0x00011C28;
    internal const int PointsOffset = 68;
    internal const int ModernStatusOffset = 104;
    internal const int ModernStatusStride = 348;
    internal const int LegacyStatusStride = 28;
    internal const int ControlStride = 36;
    internal const int ControlDeltaOffset = 20;
    internal const int ModernPointCapacity = 255;
    internal const int LegacyPointCapacity = 80;

    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    internal static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    internal static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    internal static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    internal static byte[] Buffer(int size, uint version)
    {
        var bytes = new byte[size];
        Put(bytes, 0, version);
        return bytes;
    }

    internal static byte[] Request(int size, uint version, byte[] info, int maskWords)
    {
        var bytes = Buffer(size, version);
        info.AsSpan(4, maskWords * sizeof(uint)).CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    internal static bool HasPoint(byte[] info, int id) => (U32(info, 4 + id / 32 * 4) & (1u << (id % 32))) != 0;

    internal static NvidiaGpuVfCurveReadResult Decode(string identity, string abi, byte[] info,
        byte[] status, byte[] control, int scalarKhz, int? minimumKhz, int? maximumKhz, bool simulated = false)
    {
        var modern = abi == NvidiaGpuVfCurveAbis.Modern;
        if (!modern && abi != NvidiaGpuVfCurveAbis.Legacy)
            return new(null, "nvidia-vf-unknown-abi");
        if (info.Length != InfoSize || control.Length != ControlSize
            || status.Length != (modern ? ModernStatusSize : LegacyStatusSize)
            || U32(info, 0) != InfoVersion || U32(control, 0) != ControlVersion
            || U32(status, 0) != (modern ? ModernStatusVersion : LegacyStatusVersion))
            return new(null, "nvidia-vf-unrecognized-buffer-layout");
        var points = new List<NvidiaGpuVfCurvePoint>();
        var count = modern ? ModernPointCapacity : LegacyPointCapacity;
        for (var id = 0; id < count; id++)
        {
            // Walk actual mask bits, never popcount then compress point identities.
            if (!HasPoint(info, id)) continue;
            if (modern && (U32(info, PointsOffset + id * 24) != 0 || info[PointsOffset + id * 24 + 4] != 1)) continue;
            if (!HasPoint(status, id) || !HasPoint(control, id)) return new(null, "nvidia-vf-inconsistent-point-masks");
            var offset = (modern ? ModernStatusOffset : PointsOffset) + id * (modern ? ModernStatusStride : LegacyStatusStride);
            if (modern && (U32(status, offset) != 0 || U32(control, PointsOffset + id * ControlStride) != 0))
                return new(null, "nvidia-vf-unsupported-point-type");
            var current = U32(status, offset + 4);
            var voltage = U32(status, offset + 8);
            var total = (long)I32(control, PointsOffset + id * ControlStride + ControlDeltaOffset) + scalarKhz;
            var baseFrequency = modern && status[36] == 1 ? U32(status, offset + 12) : (long)current - total;
            if (modern && status[36] > 1) return new(null, "nvidia-vf-unknown-base-tuple-support");
            if (modern && status[36] == 1) voltage = U32(status, offset + 16);
            if (voltage == 0 || current == 0 || baseFrequency <= 0 || total < int.MinValue || total > int.MaxValue)
                return new(null, "nvidia-vf-invalid-point-values");
            points.Add(new(id, voltage, baseFrequency, current, (int)total, minimumKhz, maximumKhz));
        }
        if (points.Count == 0) return new(null, "nvidia-vf-no-voltage-based-programmable-points");
        return new(new(identity, abi, points.AsReadOnly(), simulated,
            simulated ? null : NvidiaGpuVfCurveAbis.LiveWriteUnavailable, scalarKhz));
    }
}
