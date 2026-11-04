namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed partial class NvidiaGpuControlResource
{
    private readonly INvidiaGpuVfCurveAdapter? vfCurveAdapter;
    private readonly object vfCurveGate = new();
    private readonly Dictionary<string, NvidiaGpuVfCurveSimulatedState> vfCurveBaselines = new(StringComparer.Ordinal);
    public bool IsVfCurveSimulation => vfCurveAdapter is NvidiaGpuVfCurveSimulatedAdapter;

    /// <summary>Pure ownership fact; a failed rollback retains its original baseline until release succeeds.</summary>
    public bool HasVfCurveOwnership(NvidiaControlObject target)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        lock (vfCurveGate) return vfCurveBaselines.ContainsKey(target.Id);
    }

    /// <summary>Explicitly forget a disconnected instance. This never restores or otherwise accesses hardware.</summary>
    public void ForgetVfCurveOwnership(string objectId)
    {
        CheckLifetime();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        lock (vfCurveGate) vfCurveBaselines.Remove(objectId);
    }

    /// <summary>
    /// Compatibility only: releases a pre-curve persisted scalar ownership, without inventing a curve baseline.
    /// Remove when upgrades from pre-curve versions are no longer supported. The neutral scalar is zero:
    /// LACT a844b05f2cf67b93292f049f9abf03d1e9acb885,
    /// lact-daemon/src/server/gpu_controller/nvidia.rs reset_clocks (MIT), resets clock offsets to zero.
    /// This reuses RM's existing scalar NVAPI path, then verifies its existing scalar Get; it does not
    /// grant production V/F table-write capability or promise arbitrary third-party state restoration.
    /// </summary>
    public async Task<bool> RestoreLegacyCoreOffsetAsync(NvidiaControlObject target,
        NvidiaGpuVfCurveTransactionOptions options, CancellationToken cancellationToken)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ReadbackToleranceKhz < 0) return false;
        var capability = new NvidiaControlCapability(CoreClockOffsetCapabilityId);
        var outcome = await WriteAsync(target, capability,
            new(CoreClockOffsetCapabilityId, Number: 0, Unit: NvidiaControlUnits.Megahertz), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != NvidiaControlStatuses.Applied) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var actual = await ReadAsync(target, capability, cancellationToken).ConfigureAwait(false);
        return actual is { Number: { } offset, Unit: NvidiaControlUnits.Megahertz }
            && double.IsFinite(offset) && Math.Abs(offset) <= options.ReadbackToleranceKhz / 1000d;
    }

    public NvidiaGpuVfCurveReadResult ReadVfCurve(NvidiaControlObject target)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        lock (vfCurveGate)
        {
            if (vfCurveAdapter is not null) return vfCurveAdapter.Read(target);
            if (!target.IsDiscreteNvidia) return new(null, "nvidia-vf-device-not-nvidia-discrete");
            if (target.AdapterKey is not { } key || key == 0 || target.Inventory is not { } inventory)
                return new(null, "nvidia-vf-stable-device-identity-required");
            var handle = bridge.FindHandleByIdentity(inventory, key);
            if (!handle.HasValue) return new(null, "nvidia-vf-device-identity-unresolved");
            var read = bridge.ReadVfCurve(handle.Value, target.Id);
            return read.IsUnsupported ? bridge.ReadScalarVfCurve(handle.Value, target.Id) : read;
        }
    }

    /// <summary>Only the sealed, memory-only simulator can execute this transaction.</summary>
    public NvidiaGpuVfCurveTransactionResult WriteVfCurve(NvidiaControlObject target,
        NvidiaGpuVfCurveSnapshot expected, IReadOnlyList<NvidiaGpuVfCurveOffset> points,
        NvidiaGpuVfCurveTransactionOptions options)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(options);
        lock (vfCurveGate)
        {
            if (vfCurveAdapter is not NvidiaGpuVfCurveSimulatedAdapter simulated)
                return new(false, NvidiaGpuVfCurveAbis.LiveWriteUnavailable);
            var result = simulated.Apply(target, expected, points.ToArray(), options, out var baseline);
            if (baseline is not null) vfCurveBaselines.TryAdd(target.Id, baseline);
            return result;
        }
    }

    public NvidiaGpuVfCurveTransactionResult RestoreVfCurve(NvidiaControlObject target,
        NvidiaGpuVfCurveTransactionOptions options)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        lock (vfCurveGate)
        {
            if (vfCurveAdapter is not NvidiaGpuVfCurveSimulatedAdapter simulated)
                return new(false, NvidiaGpuVfCurveAbis.LiveWriteUnavailable);
            if (!vfCurveBaselines.TryGetValue(target.Id, out var baseline)) return new(false, "nvidia-vf-baseline-unavailable");
            var result = simulated.Restore(target, baseline, options);
            if (result.Applied) vfCurveBaselines.Remove(target.Id);
            return result;
        }
    }
}
