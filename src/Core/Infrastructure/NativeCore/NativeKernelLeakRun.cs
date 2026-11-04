using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed unsafe partial class NativeKernelLeakRun
{
    private readonly KernelLeakObserver owner;
    private readonly IKernelLeakSampleSource source;
    private readonly KernelLeakOptions options;
    private readonly BusinessBlockRunContext context;
    private readonly ResourceManagerLedgerOwner? ledger;

    internal NativeKernelLeakRun(KernelLeakObserver owner, IKernelLeakSampleSource source,
        IKernelLeakAttributionReader attribution, KernelLeakOptions options, BusinessBlockRunContext context,
        ResourceManagerLedgerOwner? ledger)
        => (this.owner, this.source, this.options, this.context, this.ledger) = (owner, source, options, context, ledger);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        internal uint Status, Failure;
        internal long Captured, Started, FailedAt, PreviousStart, PreviousAt;
        internal double Elapsed;
        internal ulong Frames, SeriesBytes, PeakBytes;
        internal uint Samples, HasStart, HasPrevious, PreviousFailure, Unavailable, Unrecorded, RecordedSuspects, Holders;
        internal double JudgmentMilliseconds, AttributionMilliseconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Frame
    {
        internal long Ticks;
        internal uint Available, UnavailableCount;
        internal NativeKernelLeakSeriesAbi.Sample* Samples;
        internal nuint Count;
        internal NativeKernelLeakItems.Key* Unavailable;
        internal nuint UnavailableLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration { internal NativeKernelLeakSeriesAbi.Options Options; internal uint Interval, ItemCapacity; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, Frame*, uint> Next;
        internal delegate* unmanaged[Cdecl]<nint, Snapshot*, NativeKernelLeakSeriesAbi.Suspect*, nuint, NativeKernelLeakItems.Key*, nuint, uint> Prepare;
        internal delegate* unmanaged[Cdecl]<nint, Snapshot*, uint> Publish;
        internal delegate* unmanaged[Cdecl]<nint, uint> Started;
        internal delegate* unmanaged[Cdecl]<nint, uint, uint> Wait;
        internal delegate* unmanaged[Cdecl]<nint, long> Now;
        internal delegate* unmanaged[Cdecl]<nint, double> Clock;
    }
    private IAsyncEnumerator<KernelLeakSampleFrame>? iterator;
    private KernelLeakSampleFrame? current;
    private ExceptionDispatchInfo? fatal;
    private NativeKernelLeakSeriesAbi.Sample* input;
    private uint inputCapacity;
    internal void Run()
    {
        using var localLedger = ledger is null ? new ResourceManagerLedgerOwner() : null;
        var configuration = new Configuration { Options = NativeKernelLeakSeriesAbi.ToOptions(options),
            Interval = (uint)options.IntervalMilliseconds, ItemCapacity = (uint)options.ReportedItemCapacity };
        var seriesBytes = NativeKernelLeakSeriesAbi.Size(in configuration.Options);
        var nativeRunBytes = (seriesBytes + 7) & ~(nuint)7;
        nativeRunBytes += checked((nuint)options.MaximumHolders * (nuint)(sizeof(NativeKernelLeakSeriesAbi.Suspect) + sizeof(NativeKernelLeakItems.Key)));
        // One extra slot is enough for an explicitly over-capacity input; series rejects it before reading rows.
        inputCapacity = checked((uint)options.MaximumHolders + 1);
        var allBytes = nativeRunBytes + checked((nuint)inputCapacity * (nuint)sizeof(NativeKernelLeakSeriesAbi.Sample));
        var memoryOwner = ledger ?? localLedger!;
        // Even the smallest test plan uses the Ledger-backed region required
        // for the run-scoped retained lease, rather than small managed scratch.
        using var scratch = memoryOwner.CreateScratch(Math.Max(checked((ulong)allBytes + 7), memoryOwner.NativePageSize));
        scratch.WorkArea = "kernel-leak-observation";
        scratch.Rows = $"{options.MaximumHolders} holders x {options.SeriesCapacity} points and bounded input/output";
        // This run is the use scope. End-use and scratch close follow subscription disposal.
        using var lease = scratch.Acquire();
        using (var initialize = new ComputeScratchView(lease)) { }
        lease.PublishAndRetain();
        using var view = new ComputeScratchView(lease);
        var region = view.AlignedRegion(0, allBytes, 8);
        input = (NativeKernelLeakSeriesAbi.Sample*)((byte*)region + nativeRunBytes);
        var reference = GCHandle.Alloc(this);
        try
        {
            var callbacks = new Callbacks { Context = GCHandle.ToIntPtr(reference), Next = &Next, Prepare = &Prepare,
                Publish = &Publish, Started = &Started, Wait = &Wait, Now = &Now, Clock = &Clock };
            var result = RunNative(region, nativeRunBytes, in configuration, in callbacks);
            fatal?.Throw();
            if (result != 0) throw new InvalidDataException($"Native kernel leak observation failed ({result}).");
        }
        finally
        {
            try { DisposeIterator(); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            finally { reference.Free(); input = null; current = null; }
        }
    }
    private void DisposeIterator()
    {
        var previous = iterator; iterator = null;
        previous?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static NativeKernelLeakRun Get(nint value) => (NativeKernelLeakRun)GCHandle.FromIntPtr(value).Target!;
    private uint Error(Exception error)
    {
        if (error is OperationCanceledException && context.StopNotification.IsCancellationRequested) return 2;
        if (KernelLeakObserver.IsObservationFailure(error)) return KernelLeakObserver.FailureCode(error);
        fatal = ExceptionDispatchInfo.Capture(error); return 6;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Next(nint reference, Frame* output)
    {
        var run = Get(reference);
        try
        {
            run.context.StopNotification.ThrowIfCancellationRequested();
            run.iterator ??= run.source.SubscribeAsync("kernel-leak-observer", TimeSpan.FromMilliseconds(run.options.IntervalMilliseconds),
                run.context.StopNotification).GetAsyncEnumerator(run.context.StopNotification);
            if (!run.iterator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) { run.DisposeIterator(); return 1; }
            var frame = run.current = run.iterator.Current;
            var count = frame.Available ? Math.Min(frame.Samples.Count, checked((int)run.inputCapacity)) : 0;
            // An oversized frame is rejected by native code before rows are
            // read. Invalid identities are frame failures, not subscription
            // failures: preserve the actual timestamp and the live iterator.
            if (count < run.inputCapacity)
                for (var i = 0; i < count; i++)
                {
                    try { run.input[i] = NativeKernelLeakSeriesAbi.ToSample(frame.Samples[i]); }
                    catch (InvalidDataException) { run.input[i] = default; }
                }
            // Unavailable identities are consumed from the borrowed managed
            // frame during Prepare; the native observation loop does not read
            // them and must not impose an extra per-frame policy here.
            *output = new() { Ticks = frame.CapturedAt.UtcTicks, Available = frame.Available ? 1u : 0,
                UnavailableCount = unchecked((uint)frame.UnavailableHolderCount), Samples = run.input, Count = (nuint)count,
                Unavailable = null, UnavailableLength = 0 };
            return 0;
        }
        catch (Exception error)
        {
            try { run.DisposeIterator(); } catch (Exception disposal) { return run.Error(disposal); }
            return run.Error(error);
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Prepare(nint reference, Snapshot* snapshot, NativeKernelLeakSeriesAbi.Suspect* suspects, nuint count,
        NativeKernelLeakItems.Key* judged, nuint judgedCount)
    {
        var run = Get(reference);
        try
        {
            run.owner.PrepareNative(ref *snapshot, new(suspects, checked((int)count)), new(judged, checked((int)judgedCount)),
                run.current!, run.options, run.context.StopNotification);
            return 0;
        }
        catch (Exception error) { return run.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Publish(nint reference, Snapshot* snapshot)
    {
        var run = Get(reference);
        try { run.owner.PublishNative(*snapshot, run.options); return 0; }
        catch (Exception error) { return run.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Started(nint reference)
    {
        var run = Get(reference);
        try { run.context.ReportRunning(); return 0; } catch (Exception error) { return run.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Wait(nint reference, uint interval)
    {
        var run = Get(reference);
        try { Task.Delay(checked((int)interval), run.context.StopNotification).GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { return run.Error(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Now(nint reference) => DateTimeOffset.UtcNow.UtcTicks;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double Clock(nint reference) => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).TotalMilliseconds;
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint RunNative(nint region, nuint length, in Configuration config, in Callbacks callbacks);
}
