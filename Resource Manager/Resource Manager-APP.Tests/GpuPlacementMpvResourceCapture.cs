using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

internal sealed class GpuPlacementMpvResourceCapture(string root, ulong topology) : IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(nint handle, out ulong birth, out ulong exit, out ulong kernel, out ulong user);

    private readonly NativePdhCollector collector = new(new NativePdhApi(), TimeSpan.Zero, TimeProvider.System, TimeSpan.Zero);
    private readonly List<object> samples = [];
    private Process? process;

    internal async Task CaptureAsync(string stage, GpuPlacementProcessInstance identity, bool exited = false)
    {
        process ??= Process.GetProcessById(identity.ProcessId);
        var currentCount = 0;
        for (var index = 0; index < 3; index++)
        {
            if (index != 0) await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.True(GetProcessTimes(process.Handle, out var birth, out _, out _, out _));
            Assert.Equal(identity.ProcessStartKey, birth);
            process.Refresh();
            Assert.Equal(exited, process.HasExited);
            var before = Stopwatch.GetTimestamp();
            var result = collector.Read(topology);
            var after = Stopwatch.GetTimestamp();
            Assert.True(GetProcessTimes(process.Handle, out var birthAfter, out _, out _, out _));
            Assert.Equal(birth, birthAfter);
            process.Refresh();
            Assert.Equal(exited, process.HasExited);
            var frame = result.Snapshot;
            var memory = frame?.MemoryRows.Where(row => row.ProcessId == identity.ProcessId).Select(row => new
            {
                row.AdapterLuid, row.ProcessId, row.DedicatedBytes
            }).ToArray();
            var engines = frame?.EngineRows.Where(row => row.ProcessId == identity.ProcessId).Select(row => new
            {
                row.AdapterLuid, row.ProcessId, engineClass = row.EngineClass.ToString(), row.UsagePercent
            }).ToArray();
            var current = result.ResultCode == NativePdhResultCode.Ok
                && frame?.GpuMemoryObservation.Availability == NativePdhProviderAvailability.Available
                && frame.GpuEngineObservation.Availability == NativePdhProviderAvailability.Available;
            if (current) currentCount++;
            samples.Add(new
            {
                stage, index, before, after, qpcFrequency = Stopwatch.Frequency, identity, birth, birthAfter, exited,
                availability = result.Availability.ToString(), resultCode = result.ResultCode.ToString(), result.NativeStatus,
                frame = frame is null ? null : new
                {
                    frame.Sequence, frame.TopologyGeneration, frame.CapturedAt, flags = frame.FrameFlags.ToString(),
                    engine = Observation(frame.GpuEngineObservation), memory = Observation(frame.GpuMemoryObservation)
                },
                memoryRows = memory, engineRows = engines,
                workingSetBytes = exited ? (long?)null : process.WorkingSet64,
                privateBytes = exited ? (long?)null : process.PrivateMemorySize64,
                cpuTimeTicks = exited ? (long?)null : process.TotalProcessorTime.Ticks
            });
            await File.WriteAllTextAsync(Path.Combine(root, "mpv-resource-samples.json"), JsonSerializer.Serialize(new
            {
                route = "explicit-existing-native-collector-diagnostic", publishedSubscription = false,
                sharedGpuMemoryMeasured = false, missingRowsAreZero = false, topology, samples
            }));
        }
        Assert.True(currentCount >= 2, $"{stage}: only {currentCount}/3 current GPU domain frames");
    }

    private static object Observation(NativePdhDatasetObservation value) => new
    {
        availability = value.Availability.ToString(), value.Generation, value.ObservedAt
    };

    public void Dispose()
    {
        collector.Dispose();
        process?.Dispose();
    }
}
