using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public partial class NvidiaNvapiControlBridge
{
    public virtual NvidiaGpuVfCurveReadResult ReadScalarVfCurve(IntPtr gpuHandle, string deviceIdentity)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
                return new(null, "nvidia-vf-scalar-get-interface-unavailable");
            var getPstates = bindings.GetPstates20;
            if (getPstates is null) return new(null, "nvidia-vf-scalar-get-interface-unavailable");
            return ReadScalarVfCurveBuffer(deviceIdentity, bytes =>
            {
                var memory = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                    var code = getPstates(gpuHandle, memory);
                    Marshal.Copy(memory, bytes, 0, bytes.Length);
                    return code;
                }
                finally { Marshal.FreeHGlobal(memory); }
            });
        }
    }

    // Pstates20 V1 numerical layout matches existing ReadClockOffsetMhz and NvAPIWrapper
    // 2a41eb4d6b1bceff5dbb33a3b5ea70bd582214a7 Native/GPU/Structures/
    // PerformanceStates20InfoV1.cs and PerformanceStates20ClockEntryV1.cs (LGPL-3.0; layout facts only).
    internal static NvidiaGpuVfCurveReadResult ReadScalarVfCurveBuffer(string identity, Func<byte[], int> get)
    {
        var buffer = NvidiaGpuVfCurveAbi.Buffer(Pstates20V1Size, MakeVersion(Pstates20V1Size, 1));
        var code = get(buffer);
        if (code != NvapiOk) return ReadFailure("scalar", code, allowScalarFallback: false);
        if (NvidiaGpuVfCurveAbi.U32(buffer, 0) != MakeVersion(Pstates20V1Size, 1))
            return new(null, "nvidia-vf-unknown-pstate-version");
        var pstateCount = NvidiaGpuVfCurveAbi.U32(buffer, Pstates20NumPstatesOffset);
        var clockCount = NvidiaGpuVfCurveAbi.U32(buffer, Pstates20NumClocksOffset);
        if (pstateCount > 16 || clockCount > 8) return new(null, "nvidia-vf-invalid-pstate-counts");
        for (var p = 0; p < pstateCount; p++)
        {
            var po = Pstates20HeaderSize + p * Pstate20EntrySize;
            if (NvidiaGpuVfCurveAbi.U32(buffer, po) != 0) continue;
            for (var c = 0; c < clockCount; c++)
            {
                var co = po + Pstate20ClocksOffset + c * Pstate20ClockEntrySize;
                if (NvidiaGpuVfCurveAbi.U32(buffer, co) != GraphicsClockDomain) continue;
                var scalar = NvidiaGpuVfCurveAbi.I32(buffer, co + Pstate20ClockFrequencyDeltaOffset);
                var min = NvidiaGpuVfCurveAbi.I32(buffer, co + Pstate20ClockFrequencyDeltaOffset + 4);
                var max = NvidiaGpuVfCurveAbi.I32(buffer, co + Pstate20ClockFrequencyDeltaOffset + 8);
                if (min >= max) return new(null, "nvidia-vf-scalar-range-unavailable");
                // Voltage and absolute frequency do not exist in a scalar interface; zero denotes
                // the single full-domain binding, never a fabricated drawable hardware curve.
                NvidiaGpuVfCurvePoint point = new(0, 0, 0, 0, scalar, min, max);
                return new(new(identity, NvidiaGpuVfCurveAbis.Scalar, Array.AsReadOnly(new[] { point }),
                    false, NvidiaGpuVfCurveAbis.LiveWriteUnavailable, scalar));
            }
        }
        return new(null, "nvidia-vf-p0-graphics-scalar-unavailable");
    }

    /// <summary>Explicit Get only; constructing the bridge does not invoke this path.</summary>
    public virtual NvidiaGpuVfCurveReadResult ReadVfCurve(IntPtr gpuHandle, string deviceIdentity)
    {
        lock (gate)
        {
            if (!EnsureInitialized()) return new(null, "nvidia-vf-nvapi-unavailable");
            if (bindings.GetVfInfo is null || bindings.GetVfStatus is null || bindings.GetVfControl is null)
                return new(null, "nvidia-vf-get-interface-unavailable", IsUnsupported: true);
            return ReadVfCurveBuffers(deviceIdentity, (id, bytes) =>
            {
                var function = id switch
                {
                    NvidiaGpuVfCurveAbi.GetInfoId => bindings.GetVfInfo,
                    NvidiaGpuVfCurveAbi.GetStatusId => bindings.GetVfStatus,
                    NvidiaGpuVfCurveAbi.GetControlId => bindings.GetVfControl,
                    NvapiGpuGetPstates20Id => bindings.GetPstates20,
                    _ => null
                };
                if (function is null) return NvidiaGpuVfCurveAbi.NotSupported;
                var memory = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                    var result = function(gpuHandle, memory);
                    Marshal.Copy(memory, bytes, 0, bytes.Length);
                    return result;
                }
                finally { Marshal.FreeHGlobal(memory); }
            });
        }
    }

    // LACT a844b05f2cf67b93292f049f9abf03d1e9acb885, nvidia.rs read_vf_curve and
    // nvidia/nvapi.rs clock_client_clk_vf_points_get_* (MIT): Info -> Status -> Control.
    // No Set delegate exists in this path. Legacy fallback is only on INCOMPATIBLE_STRUCT_VERSION;
    // arbitrary errors or unexpected returned versions must never select a guessed layout.
    internal static NvidiaGpuVfCurveReadResult ReadVfCurveBuffers(string identity, Func<uint, byte[], int> get)
    {
        var info = NvidiaGpuVfCurveAbi.Buffer(NvidiaGpuVfCurveAbi.InfoSize, NvidiaGpuVfCurveAbi.InfoVersion);
        var code = get(NvidiaGpuVfCurveAbi.GetInfoId, info);
        if (code != NvapiOk) return ReadFailure("info", code);
        if (NvidiaGpuVfCurveAbi.U32(info, 0) != NvidiaGpuVfCurveAbi.InfoVersion)
            return new(null, "nvidia-vf-unknown-info-version");

        var status = NvidiaGpuVfCurveAbi.Request(NvidiaGpuVfCurveAbi.ModernStatusSize,
            NvidiaGpuVfCurveAbi.ModernStatusVersion, info, 8);
        code = get(NvidiaGpuVfCurveAbi.GetStatusId, status);
        var legacy = code == NvidiaGpuVfCurveAbi.IncompatibleVersion;
        if (legacy)
        {
            status = NvidiaGpuVfCurveAbi.Request(NvidiaGpuVfCurveAbi.LegacyStatusSize,
                NvidiaGpuVfCurveAbi.LegacyStatusVersion, info, 4);
            code = get(NvidiaGpuVfCurveAbi.GetStatusId, status);
        }
        if (code != NvapiOk) return ReadFailure(legacy ? "legacy-status" : "status", code);
        if (NvidiaGpuVfCurveAbi.U32(status, 0) != (legacy
            ? NvidiaGpuVfCurveAbi.LegacyStatusVersion : NvidiaGpuVfCurveAbi.ModernStatusVersion))
            return new(null, "nvidia-vf-unknown-status-version");
        var control = NvidiaGpuVfCurveAbi.Request(NvidiaGpuVfCurveAbi.ControlSize,
            NvidiaGpuVfCurveAbi.ControlVersion, info, legacy ? 4 : 8);
        code = get(NvidiaGpuVfCurveAbi.GetControlId, control);
        if (code != NvapiOk) return ReadFailure("control", code);
        if (NvidiaGpuVfCurveAbi.U32(control, 0) != NvidiaGpuVfCurveAbi.ControlVersion)
            return new(null, "nvidia-vf-unknown-control-version");

        // Existing P0 graphics offset/range is a separate scalar. Preserve it in the snapshot;
        // curve offsets represent scalar + per-point delta. Never pretend a missing scalar is zero.
        var pstate = NvidiaGpuVfCurveAbi.Buffer(Pstates20V1Size, MakeVersion(Pstates20V1Size, 1));
        code = get(NvapiGpuGetPstates20Id, pstate);
        if (code != NvapiOk) return ReadFailure("scalar-baseline", code, allowScalarFallback: false);
        if (NvidiaGpuVfCurveAbi.U32(pstate, 0) != MakeVersion(Pstates20V1Size, 1))
            return new(null, "nvidia-vf-unknown-pstate-version");
        var pstateCount = NvidiaGpuVfCurveAbi.U32(pstate, Pstates20NumPstatesOffset);
        var clockCount = NvidiaGpuVfCurveAbi.U32(pstate, Pstates20NumClocksOffset);
        if (pstateCount > 16 || clockCount > 8) return new(null, "nvidia-vf-invalid-pstate-counts");
        for (var p = 0; p < pstateCount; p++)
        {
            var po = Pstates20HeaderSize + p * Pstate20EntrySize;
            if (NvidiaGpuVfCurveAbi.U32(pstate, po) != 0) continue;
            for (var c = 0; c < clockCount; c++)
            {
                var co = po + Pstate20ClocksOffset + c * Pstate20ClockEntrySize;
                if (NvidiaGpuVfCurveAbi.U32(pstate, co) != GraphicsClockDomain) continue;
                var scalar = NvidiaGpuVfCurveAbi.I32(pstate, co + Pstate20ClockFrequencyDeltaOffset);
                var min = NvidiaGpuVfCurveAbi.I32(pstate, co + Pstate20ClockFrequencyDeltaOffset + 4);
                var max = NvidiaGpuVfCurveAbi.I32(pstate, co + Pstate20ClockFrequencyDeltaOffset + 8);
                return NvidiaGpuVfCurveAbi.Decode(identity, legacy ? NvidiaGpuVfCurveAbis.Legacy : NvidiaGpuVfCurveAbis.Modern,
                    info, status, control, scalar, min < max ? min : null, min < max ? max : null);
            }
        }
        return new(null, "nvidia-vf-p0-graphics-scalar-unavailable");
    }

    private static NvidiaGpuVfCurveReadResult ReadFailure(string operation, int code, bool allowScalarFallback = true)
        => new(null, $"nvidia-vf-{operation}-failed:{code}", code,
            allowScalarFallback && code == NvidiaGpuVfCurveAbi.NotSupported);
}
