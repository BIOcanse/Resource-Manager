using System.Text.Json;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.SystemHealth;

namespace ResourceManager.App.Infrastructure.FreezeReports;

// Reads only original publications. There is no recorder clock or alternate Windows reader.
internal sealed class FreezeRecorderSampleAdapter(IMetricSnapshotPushSource hardware,
    IResourceBreakdownPushSource processes, ISystemInterruptSnapshotPushSource interrupts)
{
    private const string PrivateCommit = "process_all_private_commit_actual";
    private const string ImagePath = "process_all_image_path_actual";
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "memory_all_commit_bytes", "memory_all_commit_limit_bytes", "memory_all_physical_total_bytes",
        "memory_all_physical_available_bytes", "memory_all_kernel_paged_pool_bytes", "memory_all_kernel_nonpaged_pool_bytes",
        "storage_all_queue_length_actual", "storage_all_latency_actual", "storage_all_completed_operations_actual"
    };

    internal async Task RunAsync(TimeSpan interval, Action<byte[]> submit, Action<ResourceBreakdownSnapshot> updateProcesses,
        Func<double?> immediateThreshold, Action<Exception> reportInterruptFailure, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var interrupt = new InterruptPublication(null, "not-published");
        async Task InterruptPublications()
        {
            try
            {
                await foreach (var value in interrupts.SubscribeAsync("freeze-recorder.interrupts", interval, lifetime.Token))
                    Volatile.Write(ref interrupt, new(value, null));
                lifetime.Token.ThrowIfCancellationRequested();
                throw new IOException("The freeze recorder interrupt publication source ended.");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                Volatile.Write(ref interrupt, new(null, "source-unavailable"));
                reportInterruptFailure(error);
            }
        }
        async Task ProcessPublications()
        {
            var request = ResourceBreakdownSampleRequest.ForResourceTable([PrivateCommit, ImagePath], new Dictionary<string, string>());
            await foreach (var value in processes.SubscribeAsync("freeze-recorder.processes", request, interval, lifetime.Token))
                updateProcesses(value);
        }
        async Task HardwarePublications()
        {
            HardwareMetricSnapshot? catalog = null;
            await foreach (var value in hardware.SubscribeAsync("freeze-recorder.catalog", MetricSampleRequest.CatalogProbe, interval, lifetime.Token))
            {
                if (value.FieldBindings.Count == 0) continue;
                catalog = value;
                break;
            }
            if (catalog is null) return;
            DateTimeOffset? last = null;
            while (!lifetime.IsCancellationRequested)
            {
                var request = MetricDeviceFields.SelectBound(catalog, null, Fields);
                var topologyChanged = false;
                await foreach (var value in hardware.SubscribeAsync("freeze-recorder", request, interval, lifetime.Token))
                {
                    if (last is not { } previous || value.CapturedAt > previous)
                    {
                        last = value.CapturedAt;
                        var publishedInterrupt = Volatile.Read(ref interrupt);
                        submit(Encode(value, immediateThreshold(), publishedInterrupt.Snapshot, publishedInterrupt.UnavailableReason));
                    }
                    // Topology comes with original publications; re-expand the same named
                    // subscription when a physical disk appears or disappears.
                    var expanded = MetricDeviceFields.SelectBound(value, null, Fields);
                    if (expanded.CacheKey != request.CacheKey) { catalog = value; topologyChanged = true; break; }
                }
                if (!topologyChanged) throw new IOException("The freeze recorder hardware publication source ended.");
            }
        }
        var processTask = ProcessPublications();
        var interruptTask = InterruptPublications();
        var hardwareTask = HardwarePublications();
        try
        {
            await Task.WhenAny(processTask, hardwareTask);
            await lifetime.CancelAsync();
            await Task.WhenAll(processTask, interruptTask, hardwareTask);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(processTask, interruptTask, hardwareTask); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
    private sealed record InterruptPublication(SystemInterruptSnapshot? Snapshot, string? UnavailableReason);

    internal static byte[] Encode(HardwareMetricSnapshot hardware, double? immediateThreshold, SystemInterruptSnapshot? interrupt,
        string? interruptUnavailableReason = null)
    {
        var devices = MetricDeviceFields.Read(hardware, fields: Fields);
        var catalogDiskIds = hardware.FieldBindings.Values
            .Select(binding => binding.Device.Address?.PhysicalDiskDeviceId)
            .OfType<int>().Where(id => id >= 0).ToHashSet();
        var disks = devices.Where(device => device.Device.Address?.PhysicalDiskDeviceId is >= 0
            && device.Fields.Any(field => field.Name == "storage_all_queue_length_actual")).ToArray();
        var recordedDiskIds = disks.Select(device => device.Device.Address!.PhysicalDiskDeviceId!.Value).ToHashSet();
        DeviceMetricFieldValue? Field(string name) => devices.SelectMany(device => device.Fields).FirstOrDefault(field => field.Name == name);
        static ulong? Integer(DeviceMetricFieldValue? field)
        {
            if (field?.IntegerValue is { Negative: false, HighBits: 0 } value) return value.LowBits;
            // Some original byte fields are float64 projections, though the source
            // values are integral. Normalize their wire representation here.
            if (field?.NumericValue is { } number && double.IsFinite(number) && number >= 0
                && number < ulong.MaxValue && Math.Truncate(number) == number) return (ulong)number;
            return null;
        }
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("timestamp_ms", hardware.CapturedAt.ToUnixTimeMilliseconds());
            WriteInteger(json, "commit_bytes", Integer(Field("memory_all_commit_bytes")));
            WriteInteger(json, "commit_limit_bytes", Integer(Field("memory_all_commit_limit_bytes")));
            WriteInteger(json, "physical_total_bytes", Integer(Field("memory_all_physical_total_bytes")));
            WriteInteger(json, "physical_available_bytes", Integer(Field("memory_all_physical_available_bytes")));
            WriteInteger(json, "paged_pool_bytes", Integer(Field("memory_all_kernel_paged_pool_bytes")));
            WriteInteger(json, "nonpaged_pool_bytes", Integer(Field("memory_all_kernel_nonpaged_pool_bytes")));
            var microseconds = interrupt?.MaximumSingleDurationMilliseconds * 1000d ?? double.NaN;
            var validInterrupt = interrupt is { Available: true } && double.IsFinite(interrupt.CpuCapacityPercent)
                && interrupt.CpuCapacityPercent is >= 0 and <= 100 && double.IsFinite(microseconds)
                && microseconds >= 0 && microseconds < ulong.MaxValue;
            WriteNumber(json, "interrupt_cpu_percent", validInterrupt ? interrupt!.CpuCapacityPercent : null);
            WriteInteger(json, "longest_interrupt_us", validInterrupt ? (ulong)microseconds : null);
            if (interrupt is not null)
            {
                json.WriteNumber("interrupt_timestamp_ms", interrupt.CapturedAt.ToUnixTimeMilliseconds());
                if (interrupt.Window.TotalMilliseconds is > 0 and <= uint.MaxValue)
                    json.WriteNumber("interrupt_window_ms", (uint)interrupt.Window.TotalMilliseconds);
            }
            if (!validInterrupt)
                json.WriteString("interrupt_unavailable_reason", interruptUnavailableReason ?? (interrupt is null ? "not-published" :
                    interrupt.Available ? "invalid-value" : "source-unavailable"));
            // The current kernel-leak owner reports process handles only. Its process
            // suspects (and its empty state) cannot establish a system pool item.
            json.WriteNull("kernel_leak_active");
            json.WriteString("kernel_leak_unavailable_reason", "system-item-not-supported");
            WriteNumber(json, "commit_immediate_percent", immediateThreshold);
            // Inventory completeness is separate from a missing completed-operation field.
            json.WriteBoolean("disks_complete", catalogDiskIds.SetEquals(recordedDiskIds));
            json.WriteStartArray("disks");
            foreach (var device in disks)
            {
                if (device.Device.Address?.PhysicalDiskDeviceId is not { } id || id < 0) continue;
                if (!device.Fields.Any(field => field.Name == "storage_all_queue_length_actual")) continue;
                json.WriteStartObject(); json.WriteNumber("physical_disk_id", id);
                WriteNumber(json, "queue_depth", device.Fields.FirstOrDefault(field => field.Name == "storage_all_queue_length_actual")?.NumericValue);
                WriteNumber(json, "latency_ms", device.Fields.FirstOrDefault(field => field.Name == "storage_all_latency_actual")?.NumericValue);
                var completed = device.Fields.FirstOrDefault(field => field.Name == "storage_all_completed_operations_actual");
                WriteInteger(json, "completed_operations", Integer(completed));
                if (Integer(completed) is null)
                    json.WriteString("completed_operations_unavailable_reason", completed is null ? "not-published" : "source-unavailable");
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
    private static void WriteInteger(Utf8JsonWriter writer, string name, ulong? value)
    { if (value is { } number) writer.WriteNumber(name, number); else writer.WriteNull(name); }
    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    { if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number); else writer.WriteNull(name); }
}
