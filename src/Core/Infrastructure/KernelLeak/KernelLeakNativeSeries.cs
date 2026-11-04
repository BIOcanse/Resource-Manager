using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// Transitional typed binding for the original series consumers/tests. The
// product run calls the native owner directly. Only a Ledger resource identity
// is retained here; each access acquires and ends its own local lease.
internal sealed unsafe class KernelLeakSeries : IDisposable
{
    private readonly ResourceManagerLedgerOwner? ownedLedger;
    private readonly IComputeScratch scratch;
    private readonly NativeKernelLeakSeriesAbi.Options options;
    private readonly ulong seriesBytes;
    private readonly ulong totalBytes;
    private readonly int capacity;
    private readonly ulong samplesOffset, suspectsOffset, judgedOffset;
    private ulong allocation;
    private bool initialized, disposed;

    internal KernelLeakSeries(KernelLeakOptions configuration, ResourceManagerLedgerOwner? ledger = null)
    {
        configuration.Validate();
        options = NativeKernelLeakSeriesAbi.ToOptions(configuration);
        capacity = configuration.MaximumHolders;
        seriesBytes = NativeKernelLeakSeriesAbi.Size(in options);
        if (seriesBytes == 0) throw new InvalidDataException("Invalid kernel leak series limits.");
        samplesOffset = checked(seriesBytes + 7) & ~7UL;
        suspectsOffset = checked(samplesOffset + (ulong)capacity * (ulong)sizeof(NativeKernelLeakSeriesAbi.Sample));
        judgedOffset = checked(suspectsOffset + (ulong)capacity * (ulong)sizeof(NativeKernelLeakSeriesAbi.Suspect));
        var length = checked(judgedOffset + (ulong)capacity * (ulong)sizeof(NativeKernelLeakItems.Key));
        totalBytes = length;
        var owner = ledger ?? (ownedLedger = new ResourceManagerLedgerOwner());
        try
        {
            var resource = owner.CreateScratch(checked(length + 7));
            resource.WorkArea = "kernel-leak-series";
            resource.Rows = $"{capacity} holders x {configuration.SeriesCapacity} points";
            scratch = resource;
        }
        catch
        {
            ownedLedger?.Dispose();
            throw;
        }
    }

    internal int Count => checked((int)Metric(0));
    internal long Bytes => checked((long)Metric(1));
    internal long PeakBytes => checked((long)Metric(2));

    private ulong Metric(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var lease = scratch.Acquire();
        using var view = new ComputeScratchView(lease);
        var region = view.AlignedRegion(0, totalBytes, 8);
        EnsureInitialized(lease, region, rejectReset: false);
        ulong* metrics = stackalloc ulong[3];
        NativeKernelLeakSeriesAbi.Metrics(region, metrics);
        lease.Complete();
        return metrics[index];
    }

    internal void Clear()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var lease = scratch.Acquire();
        using var view = new ComputeScratchView(lease);
        var region = view.AlignedRegion(0, totalBytes, 8);
        EnsureInitialized(lease, region, rejectReset: false);
        NativeKernelLeakSeriesAbi.Clear(region);
        lease.Complete();
    }

    internal KernelLeakJudgment Observe(KernelLeakSampleFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Samples.Count > capacity) throw new OverflowException("Kernel leak holder capacity exceeded.");
        using var lease = scratch.Acquire();
        using var view = new ComputeScratchView(lease);
        var region = view.AlignedRegion(0, totalBytes, 8);
        EnsureInitialized(lease, region, rejectReset: true);
        var samples = new Span<NativeKernelLeakSeriesAbi.Sample>((byte*)region + samplesOffset, frame.Samples.Count);
        for (var index = 0; index < samples.Length; index++)
            samples[index] = NativeKernelLeakSeriesAbi.ToSample(frame.Samples[index]);
        var suspects = new Span<NativeKernelLeakSeriesAbi.Suspect>((byte*)region + suspectsOffset, capacity);
        var judged = new Span<NativeKernelLeakItems.Key>((byte*)region + judgedOffset, capacity);
        fixed (NativeKernelLeakSeriesAbi.Sample* input = samples)
        fixed (NativeKernelLeakSeriesAbi.Suspect* output = suspects)
        fixed (NativeKernelLeakItems.Key* keys = judged)
        {
            NativeKernelLeakSeriesAbi.Check(NativeKernelLeakSeriesAbi.Observe(region, frame.CapturedAt.UtcTicks,
                input, (nuint)samples.Length, output, keys, (nuint)capacity, out var result));
            if (result.Suspects > (nuint)capacity || result.Judged > (nuint)capacity)
                throw new InvalidDataException("Invalid native kernel leak output count.");
            var items = new KernelLeakSuspect[checked((int)result.Suspects)];
            for (var index = 0; index < items.Length; index++)
                items[index] = NativeKernelLeakSeriesAbi.FromSuspect(in output[index]);
            var identities = new HashSet<(int Pid, string Start, KernelLeakResourceKind Kind)>();
            for (var index = 0; index < checked((int)result.Judged); index++)
                identities.Add((keys[index].Pid, NativeKernelLeakSeriesAbi.KeyText(in keys[index]), KernelLeakResourceKind.ProcessHandles));
            lease.Complete();
            return new(items, identities);
        }
    }

    private void EnsureInitialized(ComputeScratchLease lease, nint region, bool rejectReset)
    {
        if (initialized && allocation == lease.AllocationNumber && !lease.IsLoading) return;
        var replaced = initialized;
        NativeKernelLeakSeriesAbi.Check(NativeKernelLeakSeriesAbi.Initialize(region, (nuint)seriesBytes, in options));
        allocation = lease.AllocationNumber;
        initialized = true;
        lease.Complete(); // A later invalid frame must not discard the initialized region.
        if (replaced && rejectReset)
            throw new InvalidDataException("Kernel leak series workspace was reclaimed; the observation must restart.");
    }

    public void Dispose()
    {
        if (disposed) return;
        scratch.Dispose();
        ownedLedger?.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    ~KernelLeakSeries()
    {
        // Older test callers do not use IDisposable; the resource identity must
        // still close after those callers release this binding.
        try { Dispose(); } catch { }
    }
}

internal sealed record KernelLeakJudgment(IReadOnlyList<KernelLeakSuspect> Suspects,
    IReadOnlySet<(int Pid, string Start, KernelLeakResourceKind Kind)> Judged);
