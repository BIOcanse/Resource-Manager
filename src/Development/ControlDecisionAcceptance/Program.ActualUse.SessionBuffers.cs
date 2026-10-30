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

    private static KernelEtwSessionBuffers ReadKernelSessionBuffers(IServiceProvider services)
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
    private static void ValidateKernelSessionBuffers(KernelSessionBufferRow[] rows, KernelEtwSessionBuffers expected)
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
}
