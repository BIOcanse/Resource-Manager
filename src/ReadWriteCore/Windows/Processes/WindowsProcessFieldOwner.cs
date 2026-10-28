using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>Owns the original process capture gate, two CPU lanes and canonical current slots.
/// Construction and cached reads do not acquire sources; the SDK host explicitly starts and stops network, disk and CPU residency capture.</summary>
public sealed partial class WindowsProcessFieldOwner : IDisposable
{
    internal SemaphoreSlim CaptureGate { get; } = new(1, 1);
    internal ProcessFieldCurrentState Current { get; }
    public SamplingPublicationSignal Publications { get; } = new();
    private readonly EtwNetworkTcpIpCaptureOwner? network;
    private readonly EtwPhysicalDiskIoCaptureOwner? disk;
    private readonly EtwCpuResidencyCaptureOwner? residency;
    private readonly KernelEtwSessionOwner? ownedBroker;
    private ResourceManager.ReadWrite.Native.Pdh.NativePdhSession? ownedGpuSource;
    private ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource? borrowedGpuSource;
    internal void BindGpuSource(ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource source)
        => borrowedGpuSource = source;
    private ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource GetGpuSource()
        => borrowedGpuSource ?? (ownedGpuSource ??= new(new ResourceManager.ReadWrite.Native.Pdh.NativePdhApi(),
            ResourceManager.ReadWrite.Windows.Metrics.Configuration.WindowsMetricConfiguration.CreateDefault().PdhOptions,
            TimeProvider.System));
    private void ReleaseOwnedGpuSource() { ownedGpuSource?.Dispose(); ownedGpuSource = null; }
    private ProcessFieldReadWriteBackend? backend;
    internal ProcessIoRateTracker HostedIo { get; } = new();
    internal ProcessIoRateTracker DirectIo { get; } = new();
    private bool resourcesReleased;
    private int disposed;
    internal WindowsProcessFieldOwner(ProcessFieldCurrentState current, EtwCpuResidencyCaptureOwner? cpuResidency = null)
    { Current = current; residency = cpuResidency; }
    private WindowsProcessFieldOwner(IKernelEtwSessionBroker broker, KernelEtwSessionOwner? ownedBroker,
        Func<CpuResidencyCaptureConfiguration>? cpuConfiguration = null)
    {
        Current = new(); this.ownedBroker = ownedBroker;
        network = new(broker);
        disk = new(broker);
        residency = new(broker, cpuConfiguration ?? (() => CpuResidencyCaptureConfiguration.ReadWindowsTopology()));
    }
    /// <summary>The top-level composition owns one native broker if none is borrowed.
    /// Pass the same borrowed broker to all readers. No orphan policy or retry worker is installed.</summary>
    public static WindowsProcessFieldOwner CreateDefault(IKernelEtwSessionBroker? borrowedBroker = null,
        ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource? borrowedPdhSource = null,
        Func<CpuResidencyCaptureConfiguration>? cpuConfiguration = null)
    {
        if (borrowedBroker is not null) return new(borrowedBroker, null, cpuConfiguration) { borrowedGpuSource = borrowedPdhSource };
        var broker = new KernelEtwSessionOwner(new TraceEventKernelEtwSessionRuntimeFactory());
        return new(broker, broker, cpuConfiguration) { borrowedGpuSource = borrowedPdhSource };
    }
    /// <summary>Called by the SDK host's run entry to acquire this owner's network capture.
    /// Field collection and cached reads never start it. Hosted product owners use their shared reader instead.</summary>
    public void StartNetworkCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (network ?? throw new InvalidOperationException("This owner uses the hosted shared network reader and has no standalone network capture.")).Start();
    }
    /// <summary>Called when the SDK host's run returns to release its network capture.
    /// Collection stays empty until the next explicit start.</summary>
    public void StopNetworkCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (network ?? throw new InvalidOperationException("This owner uses the hosted shared network reader and has no standalone network capture.")).Stop();
    }
    /// <summary>Called by the SDK host's run entry to acquire this owner's physical-disk capture.
    /// Field collection and cached reads never start it. Hosted product owners use their shared reader instead.</summary>
    public void StartDiskCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (disk ?? throw new InvalidOperationException("This owner uses the hosted shared disk reader and has no standalone disk capture.")).Start();
    }
    /// <summary>Called when the SDK host's run returns to release its physical-disk capture.
    /// Collection stays empty until the next explicit start.</summary>
    public void StopDiskCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (disk ?? throw new InvalidOperationException("This owner uses the hosted shared disk reader and has no standalone disk capture.")).Stop();
    }
    /// <summary>Called by the SDK host's run entry to acquire this owner's CPU residency capture.
    /// Field collection and cached reads never start it. Hosted product owners use their shared reader instead.</summary>
    public void StartCpuResidencyCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (residency ?? throw new InvalidOperationException("This owner uses the hosted shared CPU residency reader and has no standalone CPU residency capture.")).EnsureStarted();
    }
    /// <summary>Called when the SDK host's run returns to release its CPU residency capture.
    /// Field collection and subscription changes never release the run's holder.</summary>
    public void StopCpuResidencyCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        (residency ?? throw new InvalidOperationException("This owner uses the hosted shared CPU residency reader and has no standalone CPU residency capture.")).Stop();
    }
    public IReadWriteBackend Backend
    {
        get { lock (Current.Gate) return backend ??= new(Current.ReadParts, Current.ReadPart,
            () => Current.Revision, async (names, ct) => await CollectAsync(names, ct)); }
    }
    public ProcessMetricFieldSnapshot? ReadCachedSnapshot()
    {
        var parts = Current.ReadParts();
        return parts.Length == 0 ? null : ProcessMetricFieldSnapshot.Merge(parts);
    }
    /// <summary>Reconcile only an owned broker. Borrowed broker retry/lifetime belongs to its composition.</summary>
    public void ReconcileOwnedEtw() => ownedBroker?.Reconcile();
    public async Task<ProcessMetricFieldSnapshot> CollectAsync(IReadOnlyList<string> names,
        CancellationToken cancellationToken = default, IMetricPublicationPermit? permit = null)
        => (await CollectCoreAsync(names, cancellationToken, permit, null)).Snapshot;

    private async Task<(ProcessMetricFieldSnapshot Snapshot, bool Committed)> CollectCoreAsync(
        IReadOnlyList<string> names, CancellationToken cancellationToken,
        IMetricPublicationPermit? permit, ProcessCollectionBorrow? borrow)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0 || names.Any(name => ProcessMetricFieldRegistry.Find(name) is null))
            throw new ArgumentException("Select canonical process fields.", nameof(names));
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await CaptureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            borrow?.RequireActive();
            var definitions = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => ProcessMetricFieldRegistry.Find(name)!).ToArray();
            var mask = definitions.Aggregate(ProcessMetricReadMask.None, static (value, field) => value | field.ReadMask);
            var includeCpu = definitions.Any(static field => field.Code == ProcessMetricFieldCode.CpuUsage);
            var textSharing = CreateTextSharing();
            IReadOnlyDictionary<WindowsProcessInventoryEntry, CapturedProcessBasic>? prepared = null;
            bool Capture(WindowsProcessInventoryEntry process, ProcessCreationRead creation, out ProcessFieldSample sample, out TimeSpan cpu, out ProcessInstanceKey? identity)
                => CaptureFieldSample(process, creation, mask, includeCpu, out sample, out cpu, out identity, prepared, textSharing);
            var batch = CaptureBatch(includeCpu, DirectCpu, (TryCaptureProcess<ProcessFieldSample>)Capture, static row => row.HasProcessorTime,
                prepare: (mask & (ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.HandleCount)) != 0
                    ? processes => prepared = ReadBasicBatch(processes, (_, _) => mask, textSharing) : null);
            if (batch.Status != ResourceManager.App.Domain.Metrics.SamplingObservationStatus.Current)
                throw new InvalidOperationException("The process inventory did not produce a complete current observation.");
            var rows = batch.Samples.Select(sample =>
            {
                var hasPercent = sample.Creation is { } birth && batch.Cpu.Percentages.TryGetValue(new(sample.ProcessId, birth), out _);
                var percent = hasPercent ? batch.Cpu.Percentages[new(sample.ProcessId, sample.Creation!.Value)] : 0;
                return new ProcessFieldProjectionSample(sample.ProcessId, sample.Creation, sample.Basic, percent, hasPercent);
            }).ToArray();
            var ioRates = mask.HasFlag(ProcessMetricReadMask.Io) ? DirectIo.Prepare(rows) : null;
            var parts = new List<ProcessMetricFieldSnapshot>();
            if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.GpuResidency))
            {
                if (borrow is null) directGpuDemand = true;
                var inventory = new ProcessMetricFieldSnapshot(batch.CapturedAt, null, [], rows.Select(row =>
                    new ProcessMetricFieldValues(row.ProcessId, row.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
                parts.Add(WindowsPdhProcessGpuFields.Read(GetGpuSource(), inventory));
            }
            if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics))
                parts.Add(WindowsProcessFieldProjection.CreateProcessBasicFields(definitions, rows, batch.CapturedAt, batch.Cpu.Interval, ioRates));
            if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic))
            {
                if (network is null) throw new InvalidOperationException("The hosted composition must collect through its original shared network reader.");
                if (borrow is null) directNetworkDemand = true;
                parts.Add(WindowsProcessFieldProjection.CreateProcessNetworkFields(names, rows, network.Read(includeObservedTraffic: true).ObservedTraffic));
            }
            if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo))
            {
                if (disk is null) throw new InvalidOperationException("The hosted composition must collect through its original shared DiskIO reader.");
                if (borrow is null) directDiskDemand = true;
                var inventory = new ProcessMetricFieldSnapshot(batch.CapturedAt, null, [], rows.Select(row =>
                    new ProcessMetricFieldValues(row.ProcessId, row.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
                parts.Add(WindowsPhysicalDiskProcessFields.Project(names, inventory, disk.Read(includeObservedTraffic: true).ObservedTraffic));
            }
            if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency))
            {
                if (residency is null) throw new InvalidOperationException("Hosted residency uses its original role6 reader.");
                residency.Collect(permit);
                var inventory = new ProcessMetricFieldSnapshot(batch.CapturedAt, null, [], rows.Select(row =>
                    new ProcessMetricFieldValues(row.ProcessId, row.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
                parts.Add(WindowsCpuResidencyProcessFields.Project(names, inventory, residency.ReadScopes(), residency.Read()));
            }
            var result = ProcessMetricFieldSnapshot.Merge(parts);
            cancellationToken.ThrowIfCancellationRequested();
            var committed = Commit(() =>
            {
                Current.Apply(names, result);
                if (ioRates is not null) DirectIo.Commit(ioRates);
            }, permit, names, borrow);
            return (result, committed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Commit(() =>
            {
                Current.Fail(names);
                if (names.Any(static name => ProcessMetricFieldRegistry.Find(name)!.ReadMask.HasFlag(ProcessMetricReadMask.Io))) DirectIo.Clear();
            }, permit, names, borrow);
            throw;
        }
        finally { CaptureGate.Release(); TryDisposeRetiredGate(); }
    }
    private bool Commit(Action commit, IMetricPublicationPermit? permit, IReadOnlyList<string> names,
        ProcessCollectionBorrow? borrow = null)
    {
        var committed = false;
        void Publish()
        {
            lock (Current.Gate)
            {
                if (Volatile.Read(ref disposed) != 0 || borrow?.IsDisposed == true) return;
                commit(); committed = true;
            }
        }
        if (permit is null) Publish(); else permit.TryPublish(Publish);
        if (committed) Publications.Publish(names.Select(name => ProcessMetricFieldRegistry.Find(name)!.DatasetId));
        return committed;
    }
    public void Dispose()
    {
        lock (Current.Gate) { if (Interlocked.Exchange(ref disposed, 1) != 0) return; }
        TryDisposeRetiredGate();
    }
    private void TryDisposeRetiredGate()
    {
        // The admitted capture drains resources on this same gate after returning
        // its observation. Retirement rejects publication without waiting for it.
        if (Volatile.Read(ref disposed) == 0 || !CaptureGate.Wait(0)) return;
        try
        {
            if (resourcesReleased) return;
            resourcesReleased = true;
            ReleaseResources();
        }
        finally { CaptureGate.Release(); }
        // Keep the managed semaphore for queued callers to observe retirement.
    }
    private void ReleaseResources()
    {
        inventoryBuffer.Dispose();
        fixedFields.Clear();
        HostedIo.Clear();
        DirectIo.Clear();
        try { network?.Dispose(); }
        finally
        {
            try { disk?.Dispose(); }
            finally
            {
                try { ReleaseOwnedGpuSource(); }
                finally
                {
                    try { residency?.Dispose(); }
                    finally { ownedBroker?.Dispose(); }
                }
            }
        }
    }
}

