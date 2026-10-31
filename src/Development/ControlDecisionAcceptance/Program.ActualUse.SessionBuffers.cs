using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    // ETW's own view of the owned kernel session's buffer pool, from the read-only ControlTrace QUERY.
    // The counters are cumulative for one session instance.
    private sealed record NativeKernelBuffers(uint BufferSizeKib, uint MinimumBuffers, uint MaximumBuffers,
        uint NumberOfBuffers, uint FreeBuffers, uint BuffersWritten, uint LogBuffersLost, uint RealTimeBuffersLost);
    private sealed record KernelSessionBufferRow(string Phase, int Samples, uint BufferSizeKib, uint MinimumBuffers,
        uint MaximumBuffers, uint MostBuffers, uint FewestFreeBuffers, uint MostBuffersInUse, uint EventsLost,
        uint LogBuffersLost, uint RealTimeBuffersLost, uint BuffersWritten);

    private static EtwSessionBuffers ReadKernelSessionBuffers(IServiceProvider services)
        => KernelEtwNativeSettings.CreateSessionBuffers(
            services.GetRequiredService<IRuntimePlanProvider>().Current.HostManager.RequirePublished().KernelEtwLimits
                ?? throw new InvalidOperationException("Kernel ETW limits are unpublished."));

    private static KernelSessionBufferRow[] SummarizeKernelSessionBuffers(IEnumerable<ActualUseSample> samples)
        => samples.Where(static sample => sample.Native.Buffers is not null)
            .GroupBy(static sample => sample.Phase)
            .Select(static phase =>
            {
                var buffers = phase.Select(static sample => sample.Native.Buffers!).ToArray();
                return new KernelSessionBufferRow(phase.Key, buffers.Length,
                    buffers.Max(static value => value.BufferSizeKib), buffers.Max(static value => value.MinimumBuffers),
                    buffers.Max(static value => value.MaximumBuffers), buffers.Max(static value => value.NumberOfBuffers),
                    buffers.Min(static value => value.FreeBuffers),
                    buffers.Max(static value => value.NumberOfBuffers - Math.Min(value.FreeBuffers, value.NumberOfBuffers)),
                    phase.Max(static sample => sample.Native.EventsLost), buffers.Max(static value => value.LogBuffersLost),
                    buffers.Max(static value => value.RealTimeBuffersLost), buffers.Max(static value => value.BuffersWritten));
            }).ToArray();

    // The declared pool must be the one ETW runs, and no phase may lose an event or a buffer.
    // ETW raises MinimumBuffers to 2 per logical processor, so that floor is also accepted.
    private static void ValidateKernelSessionBuffers(KernelSessionBufferRow[] rows, EtwSessionBuffers expected)
    {
        foreach (var phase in new[] { "b-residency", "c-tray-smart" })
            if (!rows.Any(row => row.Phase == phase))
                throw new InvalidDataException($"Phase {phase} has no kernel session buffer samples.");
        var floor = (uint)Math.Max(expected.MinimumBuffers, 2 * Environment.ProcessorCount);
        foreach (var row in rows)
        {
            if (row.BufferSizeKib != expected.BufferKib || row.MinimumBuffers != expected.MinimumBuffers && row.MinimumBuffers != floor)
                throw new InvalidDataException($"Phase {row.Phase} runs {row.MinimumBuffers} buffers of {row.BufferSizeKib} KiB, declared {expected.MinimumBuffers} of {expected.BufferKib} KiB.");
            if (row.EventsLost != 0 || row.LogBuffersLost != 0 || row.RealTimeBuffersLost != 0)
                throw new InvalidDataException($"Phase {row.Phase} lost events {row.EventsLost}, log buffers {row.LogBuffersLost}, real-time buffers {row.RealTimeBuffersLost} with at most {row.MostBuffers} buffers.");
        }
    }

    // The VidMm, GPU allocation and Present user sessions' declared pools (monitoring *_session_buffers),
    // in the order of ActualUseNativeSample.Actual: Present, GpuAllocation, VidMm.
    private static readonly string[] UserSessionNames = ["Present", "GpuAllocation", "VidMm"];
    private sealed record DeclaredUserSessionBuffers(EtwSessionBuffers Present, EtwSessionBuffers GpuAllocation, EtwSessionBuffers VidMm)
    {
        public EtwSessionBuffers For(string session) => session switch
        {
            "Present" => Present, "GpuAllocation" => GpuAllocation, "VidMm" => VidMm,
            _ => throw new ArgumentOutOfRangeException(nameof(session), session, "Unknown user session.")
        };
    }
    private sealed record UserSessionBufferRow(string Session, string Phase, int Samples, uint BufferSizeKib, uint MinimumBuffers,
        uint MaximumBuffers, uint MostBuffers, uint FewestFreeBuffers, uint MostBuffersInUse, uint EventsLost,
        uint LogBuffersLost, uint RealTimeBuffersLost, uint BuffersWritten);

    private static DeclaredUserSessionBuffers ReadUserSessionBuffers(IServiceProvider services)
    {
        var monitoring = ReadPublishedMonitoring(services);
        return new(monitoring.PresentSessionBuffers, monitoring.GpuAllocationSessionBuffers, monitoring.VidMmSessionBuffers);
    }

    // One row per session and phase in which the session existed. The GPU filter phase is
    // queried by its own loop, so its rows come from that phase's queries.
    private static UserSessionBufferRow[] SummarizeUserSessionBuffers(IEnumerable<ActualUseNativeSample> samples,
        IEnumerable<NativeKernelQuery> gpuFilterQueries)
    {
        var rows = new List<UserSessionBufferRow>();
        foreach (var phase in samples.GroupBy(static sample => sample.Phase))
            for (var index = 0; index < UserSessionNames.Length; index++)
                if (SummarizeUserSession(UserSessionNames[index], phase.Key, phase.Select(sample => sample.Actual[index])) is { } row)
                    rows.Add(row);
        if (SummarizeUserSession("GpuAllocation", "gpu-allocation-filter", gpuFilterQueries) is { } filter) rows.Add(filter);
        return rows.ToArray();
    }

    private static UserSessionBufferRow? SummarizeUserSession(string session, string phase, IEnumerable<NativeKernelQuery> queries)
    {
        var alive = queries.Where(static query => query.Exists && query.Buffers is not null).ToArray();
        if (alive.Length == 0) return null;
        var buffers = alive.Select(static query => query.Buffers!).ToArray();
        return new(session, phase, alive.Length,
            buffers.Max(static value => value.BufferSizeKib), buffers.Max(static value => value.MinimumBuffers),
            buffers.Max(static value => value.MaximumBuffers), buffers.Max(static value => value.NumberOfBuffers),
            buffers.Min(static value => value.FreeBuffers),
            buffers.Max(static value => value.NumberOfBuffers - Math.Min(value.FreeBuffers, value.NumberOfBuffers)),
            alive.Max(static query => query.EventsLost), buffers.Max(static value => value.LogBuffersLost),
            buffers.Max(static value => value.RealTimeBuffersLost), buffers.Max(static value => value.BuffersWritten));
    }

    // TraceEvent raises the buffer size to at least 1 KiB per logical processor and then divides
    // the whole-MiB pool by it, so on such machines the run differs from the declaration.
    private static (uint BufferKib, uint MinimumBuffers) ExpectedRunningPool(EtwSessionBuffers declared)
    {
        var size = (uint)Math.Max(declared.BufferKib, Environment.ProcessorCount);
        return (size, (uint)(declared.PoolMib * 1024L / size));
    }

    private static string[] DescribeRaisedBufferSize(DeclaredUserSessionBuffers declared)
        => UserSessionNames.Select(session => (Session: session, Declared: declared.For(session)))
            .Where(static value => value.Declared.BufferKib < Environment.ProcessorCount)
            .Select(static value => $"{value.Session}: TraceEvent raises the declared {value.Declared.BufferKib} KiB buffers to {Environment.ProcessorCount} KiB on this {Environment.ProcessorCount}-processor machine; the validation accepts the raised size and {ExpectedRunningPool(value.Declared).MinimumBuffers} buffers.")
            .ToArray();

    // Each running pool must be the declared one (or TraceEvent's raised buffer size), and no
    // phase may lose an event or a buffer. ETW raises MinimumBuffers to 2 per logical processor,
    // so that floor is also accepted.
    private static void ValidateUserSessionBuffers(UserSessionBufferRow[] rows, DeclaredUserSessionBuffers declared)
    {
        foreach (var row in rows)
            ValidateUserSessionBufferRow(row, declared.For(row.Session));
    }

    private static void ValidateUserSessionBufferRow(UserSessionBufferRow row, EtwSessionBuffers declared)
    {
        var (size, minimum) = ExpectedRunningPool(declared);
        var floor = Math.Max(minimum, (uint)(2 * Environment.ProcessorCount));
        if (row.BufferSizeKib != size || row.MinimumBuffers != minimum && row.MinimumBuffers != floor)
            throw new InvalidDataException($"{row.Session} in phase {row.Phase} runs {row.MinimumBuffers} buffers of {row.BufferSizeKib} KiB, declared {declared.MinimumBuffers} of {declared.BufferKib} KiB (expected {minimum} of {size} KiB here).");
        if (row.EventsLost != 0 || row.LogBuffersLost != 0 || row.RealTimeBuffersLost != 0)
            throw new InvalidDataException($"{row.Session} in phase {row.Phase} lost events {row.EventsLost}, log buffers {row.LogBuffersLost}, real-time buffers {row.RealTimeBuffersLost} with at most {row.MostBuffers} buffers.");
    }
}
