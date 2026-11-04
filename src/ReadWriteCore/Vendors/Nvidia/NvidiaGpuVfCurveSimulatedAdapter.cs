namespace ResourceManager.ReadWrite.Vendors.Nvidia;

/// <summary>
/// Memory-only transaction model. No bridge, native handle, library loader or hardware setter is held here.
/// LACT a844b05f2cf67b93292f049f9abf03d1e9acb885, nvidia.rs apply_vf_curve/reset_vf_curve
/// (MIT) establishes read-modify-whole-table and clearing programmable offsets. It does NOT establish
/// recovery of a pre-existing scalar+table pair. The neutralize/table/scalar restoration below is
/// solely a simulated protocol; it must not be connected to production Set calls without evidence.
/// </summary>
public sealed class NvidiaGpuVfCurveSimulatedAdapter : INvidiaGpuVfCurveAdapter
{
    private readonly object gate = new();
    private NvidiaGpuVfCurveSimulatedState state;
    private readonly List<string> operations = [];

    /// <summary>Inject a failure after a mutation, including partial-write scenarios. Reads fail before returning.</summary>
    public Func<string, string?>? FailOperation { get; set; }
    /// <summary>Change only the returned scalar on a named read, to model a false successful setter.</summary>
    public Func<string, int>? ReadbackScalarAdjustmentKhz { get; set; }
    public IReadOnlyList<string> Operations { get { lock (gate) return operations.ToArray(); } }

    public NvidiaGpuVfCurveSimulatedAdapter(NvidiaGpuVfCurveSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.Points.Count == 0) throw new ArgumentException("A simulation requires points.", nameof(initial));
        if (initial.Abi is not (NvidiaGpuVfCurveAbis.Modern or NvidiaGpuVfCurveAbis.Legacy or NvidiaGpuVfCurveAbis.Scalar))
            throw new ArgumentException("Unknown simulated ABI.", nameof(initial));
        var capacity = initial.Abi == NvidiaGpuVfCurveAbis.Legacy ? NvidiaGpuVfCurveAbi.LegacyPointCapacity
            : initial.Abi == NvidiaGpuVfCurveAbis.Scalar ? 1 : NvidiaGpuVfCurveAbi.ModernPointCapacity;
        if (initial.Points.Any(p => p.PointId < 0 || p.PointId >= capacity)
            || initial.Points.Select(p => p.PointId).Distinct().Count() != initial.Points.Count)
            throw new ArgumentException("Invalid simulated point identities.", nameof(initial));
        var table = NvidiaGpuVfCurveAbi.Buffer(NvidiaGpuVfCurveAbi.ControlSize, NvidiaGpuVfCurveAbi.ControlVersion);
        foreach (var point in initial.Points)
        {
            var maskOffset = 4 + point.PointId / 32 * 4;
            NvidiaGpuVfCurveAbi.Put(table, maskOffset,
                NvidiaGpuVfCurveAbi.U32(table, maskOffset) | (1u << (point.PointId % 32)));
            NvidiaGpuVfCurveAbi.Put(table, DeltaOffset(point.PointId), checked(point.FrequencyOffsetKhz - initial.ScalarOffsetKhz));
        }
        state = new(initial with { Points = Array.AsReadOnly(initial.Points.ToArray()), CanWrite = true, UnavailableReason = null }, table);
    }

    internal NvidiaGpuVfCurveSimulatedAdapter(NvidiaGpuVfCurveSnapshot initial, byte[] rawControl) : this(initial)
    {
        if (rawControl.Length != NvidiaGpuVfCurveAbi.ControlSize
            || NvidiaGpuVfCurveAbi.U32(rawControl, 0) != NvidiaGpuVfCurveAbi.ControlVersion)
            throw new ArgumentException("Invalid simulated control table.", nameof(rawControl));
        state = new(state.Snapshot, (byte[])rawControl.Clone());
    }

    internal byte[] CopyControl() { lock (gate) return (byte[])state.Control.Clone(); }

    public NvidiaGpuVfCurveReadResult Read(NvidiaControlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (gate)
        {
            if (!StringComparer.Ordinal.Equals(target.Id, state.Snapshot.DeviceIdentity)) return new(null, "nvidia-vf-device-binding-mismatch");
            return ReadState("read", out var value) is { } error ? new(null, error) : new(value!.Snapshot);
        }
    }

    internal NvidiaGpuVfCurveTransactionResult Apply(NvidiaControlObject target, NvidiaGpuVfCurveSnapshot expected,
        IReadOnlyList<NvidiaGpuVfCurveOffset> points, NvidiaGpuVfCurveTransactionOptions options,
        out NvidiaGpuVfCurveSimulatedState? retainedBaseline)
    {
        lock (gate)
        {
            retainedBaseline = null;
            if (options.ReadbackToleranceKhz < 0) return new(false, "nvidia-vf-invalid-readback-tolerance");
            var failure = ReadState("baseline-read", out var baseline);
            if (failure is not null) return new(false, failure);
            failure = Preflight(target, expected, baseline!.Snapshot, points);
            if (failure is not null) return new(false, failure);
            var desiredTable = (byte[])baseline.Control.Clone();
            var uniform = points.All(p => p.FrequencyOffsetKhz == points[0].FrequencyOffsetKhz);
            var scalarOnly = baseline.Snapshot.Abi == NvidiaGpuVfCurveAbis.Scalar;
            var scalar = uniform ? points[0].FrequencyOffsetKhz : 0;
            foreach (var point in points)
                NvidiaGpuVfCurveAbi.Put(desiredTable, DeltaOffset(point.PointId), uniform ? 0 : point.FrequencyOffsetKhz);
            var desired = Materialize(baseline, scalar, desiredTable);

            // Keep the original owner baseline until explicit release succeeds; never recapture after reassertion.
            retainedBaseline = baseline;
            if (scalarOnly)
                failure = WriteScalar("write-scalar", scalar);
            else if (uniform)
                failure = WriteTable("clear-table", desiredTable) ?? WriteScalar("write-scalar", scalar);
            else
                failure = WriteScalar("clear-scalar", 0) ?? WriteTable("write-table", desiredTable);

            NvidiaGpuVfCurveSimulatedState? observed = null;
            failure ??= ReadState("readback", out observed);
            if (failure is null && !Equivalent(desired, observed!, options.ReadbackToleranceKhz))
                failure = "nvidia-vf-readback-mismatch";
            if (failure is null) return new(true, null, Readback: observed!.Snapshot);

            var restore = RestoreCore(baseline, options);
            if (restore.Applied) retainedBaseline = null;
            return new(false, restore.Applied ? failure : failure + "; restore: " + restore.FailureReason,
                true, restore.Applied, observed?.Snapshot);
        }
    }

    internal NvidiaGpuVfCurveTransactionResult Restore(NvidiaControlObject target,
        NvidiaGpuVfCurveSimulatedState baseline, NvidiaGpuVfCurveTransactionOptions options)
    {
        lock (gate)
        {
            if (options.ReadbackToleranceKhz < 0) return new(false, "nvidia-vf-invalid-readback-tolerance");
            if (!StringComparer.Ordinal.Equals(target.Id, state.Snapshot.DeviceIdentity)
                || !SameBinding(state.Snapshot, baseline.Snapshot)) return new(false, "nvidia-vf-device-binding-mismatch");
            return RestoreCore(baseline, options);
        }
    }

    private NvidiaGpuVfCurveTransactionResult RestoreCore(NvidiaGpuVfCurveSimulatedState baseline,
        NvidiaGpuVfCurveTransactionOptions options)
    {
        // Attempt the entire old state even if one operation reports failure after partial mutation.
        var failures = new List<string>();
        if (WriteScalar("restore-clear-scalar", 0) is { } clearError) failures.Add(clearError);
        if (baseline.Snapshot.Abi != NvidiaGpuVfCurveAbis.Scalar
            && WriteTable("restore-table", baseline.Control) is { } tableError) failures.Add(tableError);
        if (WriteScalar("restore-scalar", baseline.Snapshot.ScalarOffsetKhz) is { } scalarError) failures.Add(scalarError);
        var readError = ReadState("restore-readback", out var restored);
        if (readError is not null) failures.Add(readError);
        else if (!Equivalent(baseline, restored!, options.ReadbackToleranceKhz)) failures.Add("nvidia-vf-restore-readback-mismatch");
        return failures.Count == 0 ? new(true, null, true, true, restored!.Snapshot)
            : new(false, string.Join("; ", failures), true, false, restored?.Snapshot);
    }

    private static string? Preflight(NvidiaControlObject target, NvidiaGpuVfCurveSnapshot expected,
        NvidiaGpuVfCurveSnapshot current, IReadOnlyList<NvidiaGpuVfCurveOffset> points)
    {
        if (!StringComparer.Ordinal.Equals(target.Id, current.DeviceIdentity) || !SameBinding(expected, current))
            return "nvidia-vf-device-binding-mismatch";
        if (points.Count != current.Points.Count || points.Select(p => p.PointId).Distinct().Count() != points.Count)
            return "nvidia-vf-complete-unique-point-table-required";
        var lookup = current.Points.ToDictionary(p => p.PointId);
        foreach (var point in points)
        {
            if (!lookup.TryGetValue(point.PointId, out var native) || point.VoltageMicrovolts != native.VoltageMicrovolts)
                return "nvidia-vf-point-binding-mismatch";
            if (native.MinimumOffsetKhz is not { } min || native.MaximumOffsetKhz is not { } max)
                return "nvidia-vf-offset-range-unavailable";
            if (point.FrequencyOffsetKhz < min || point.FrequencyOffsetKhz > max
                || (current.Abi != NvidiaGpuVfCurveAbis.Scalar && native.DefaultFrequencyKhz + point.FrequencyOffsetKhz <= 0))
                return "nvidia-vf-offset-out-of-range";
        }
        return null;
    }

    private static bool SameBinding(NvidiaGpuVfCurveSnapshot left, NvidiaGpuVfCurveSnapshot right)
        => StringComparer.Ordinal.Equals(left.DeviceIdentity, right.DeviceIdentity) && left.Abi == right.Abi
            && left.Points.Count == right.Points.Count && left.Points.Zip(right.Points)
                .All(p => p.First.PointId == p.Second.PointId && p.First.VoltageMicrovolts == p.Second.VoltageMicrovolts);

    private string? ReadState(string operation, out NvidiaGpuVfCurveSimulatedState? value)
    {
        operations.Add(operation);
        value = null;
        if (FailOperation?.Invoke(operation) is { } failure) return failure;
        var adjustment = ReadbackScalarAdjustmentKhz?.Invoke(operation) ?? 0;
        value = Materialize(state, checked(state.Snapshot.ScalarOffsetKhz + adjustment), (byte[])state.Control.Clone());
        return null;
    }

    private string? WriteScalar(string operation, int scalar)
    {
        operations.Add(operation);
        state = Materialize(state, scalar, (byte[])state.Control.Clone());
        return FailOperation?.Invoke(operation);
    }

    private string? WriteTable(string operation, byte[] table)
    {
        operations.Add(operation);
        state = Materialize(state, state.Snapshot.ScalarOffsetKhz, (byte[])table.Clone());
        return FailOperation?.Invoke(operation);
    }

    private static NvidiaGpuVfCurveSimulatedState Materialize(NvidiaGpuVfCurveSimulatedState source, int scalar, byte[] table)
    {
        var points = source.Snapshot.Points.Select(p =>
        {
            var offset = source.Snapshot.Abi == NvidiaGpuVfCurveAbis.Scalar ? scalar
                : checked(scalar + NvidiaGpuVfCurveAbi.I32(table, DeltaOffset(p.PointId)));
            return p with { FrequencyOffsetKhz = offset, CurrentFrequencyKhz = p.DefaultFrequencyKhz + offset };
        }).ToArray();
        return new(source.Snapshot with { ScalarOffsetKhz = scalar, Points = Array.AsReadOnly(points) }, table);
    }

    private static bool Equivalent(NvidiaGpuVfCurveSimulatedState expected, NvidiaGpuVfCurveSimulatedState actual, int tolerance)
    {
        if (!SameBinding(expected.Snapshot, actual.Snapshot)
            || Math.Abs((long)expected.Snapshot.ScalarOffsetKhz - actual.Snapshot.ScalarOffsetKhz) > tolerance
            || expected.Snapshot.Points.Zip(actual.Snapshot.Points)
                .Any(p => Math.Abs((long)p.First.FrequencyOffsetKhz - p.Second.FrequencyOffsetKhz) > tolerance)) return false;
        var unknownExpected = (byte[])expected.Control.Clone();
        var unknownActual = (byte[])actual.Control.Clone();
        foreach (var point in expected.Snapshot.Points)
        {
            var offset = DeltaOffset(point.PointId);
            if (Math.Abs((long)NvidiaGpuVfCurveAbi.I32(expected.Control, offset) - NvidiaGpuVfCurveAbi.I32(actual.Control, offset)) > tolerance)
                return false;
            unknownExpected.AsSpan(offset, sizeof(int)).Clear();
            unknownActual.AsSpan(offset, sizeof(int)).Clear();
        }
        return unknownExpected.AsSpan().SequenceEqual(unknownActual);
    }

    private static int DeltaOffset(int pointId) => NvidiaGpuVfCurveAbi.PointsOffset
        + pointId * NvidiaGpuVfCurveAbi.ControlStride + NvidiaGpuVfCurveAbi.ControlDeltaOffset;
}

internal sealed record NvidiaGpuVfCurveSimulatedState(NvidiaGpuVfCurveSnapshot Snapshot, byte[] Control);
