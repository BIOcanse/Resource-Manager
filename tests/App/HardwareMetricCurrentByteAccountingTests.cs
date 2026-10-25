using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMetricCurrentByteAccountingTests
{
    private static readonly string[] DatasetIds = ["cpu.usage", "memory.usage", "gpu.0.usage", "gpu.0.vram"];

    [Fact]
    public void RecordedDeclarationShapeHasBoundedExplicitCurrentAccounting()
    {
        using var compressed = typeof(HardwareMetricCurrentByteAccountingTests).Assembly.GetManifestResourceStream(
            typeof(HardwareMetricCurrentByteAccountingTests).Assembly.GetManifestResourceNames()
                .Single(static name => name.EndsWith("c13-metric-catalog.json.gz", StringComparison.Ordinal)))!;
        using var stream = new GZipStream(compressed, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(stream);
        var rules = json.RootElement.GetProperty("Rules").EnumerateArray().ToArray();
        var metrics = rules.GroupBy(static row => row.GetProperty("MetricHandle").GetUInt64())
            .Select(static group => group.First()).ToArray();
        Assert.Equal(1358, rules.Length);
        Assert.Equal(1352, metrics.Length);
        // This fixture contains declarations, not sample values or real machine strings.
        // Equal metadata references are shared across all captures, as at baseline HEAD.
        var scopes = metrics.Select(static row => row.GetProperty("ScopeHandle").GetUInt64()).Distinct()
            .ToDictionary(static handle => handle, static handle => $"recorded-device-{handle}");
        var sources = metrics.Select(static row => row.GetProperty("SourceHandle").GetUInt64()).Distinct()
            .ToDictionary(static handle => handle, static handle => $"recorded-source-{handle}");
        var fields = metrics.Select((row, index) => new MetricDeviceFieldBinding(
            scopes[row.GetProperty("ScopeHandle").GetUInt64()],
            new MetricFieldDefinition(MetricFieldCode.Unknown, $"recorded-field-{index}",
                MetricResource.System, MetricVendor.All, "count", "recorded")
            { ValueKind = Kind(row.GetProperty("ValueKind").GetUInt32()) },
            index < 4 ? DatasetIds[index] : $"recorded-metric-{row.GetProperty("MetricHandle").GetUInt64()}",
            sources[row.GetProperty("SourceHandle").GetUInt64()])).ToArray();
        var oldCaptures = new List<BaselineCapture>();
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var run = owner.OpenHostedRun();
        Assert.True(owner.TryCaptureHostedTicket(run, new Permit(), 1, 1, out var ticket));
        for (var round = 1; round <= 4; round++)
        {
            var bindings = fields.Select(static field => field with { }).ToDictionary(static field => field.RequestAddress);
            var old = fields.Select(field => Baseline(field, round)).ToDictionary(static value => value.Id);
            var capture = new BaselineCapture(old, bindings);
            // A successful payload and attempt are the same capture object.
            oldCaptures.Add(capture);
            Assert.True(owner.TryPublishHosted(ticket, Frame(round, bindings), MetricSampleRequest.All,
                [DatasetIds[round - 1]], null, out var publication));
            Assert.True(publication.IsSuccessful);
        }
        var composed = DatasetIds.Select((id, index) => oldCaptures[index].Items[id]).ToArray();
        var before = Graph(new object[] { oldCaptures, composed });
        var after = Graph(owner);
        Assert.Equal(5408, before.OfType<BaselineValue>().Count());
        Assert.DoesNotContain(after, static value => value is HardwareMetricSnapshot or MetricReadingValue);
        Assert.Equal(1352, after.OfType<MetricDeviceFieldBinding>().Count());
        Assert.Equal(4, after.Count(static value => value.GetType().Name == "ItemDeclaration"));
        Assert.Equal(4, owner.Read()!.Datasets.Count);
        var baselineSize = AllocationSize(() => Baseline(fields[0], 1) with { DisplayValue = "", IntegerValue = null });
        var result = new
        {
            Method = "Bounded reference-identity graph of synthetic four independent successful captures. Numeric declaration shape is recorded c13; values and metadata strings are deterministic synthetic inputs. No live heap walk.",
            Rules = rules.Length, Metrics = metrics.Length, Datasets = 4,
            MetricValueKinds = metrics.GroupBy(static row => Kind(row.GetProperty("ValueKind").GetUInt32()))
                .ToDictionary(static group => group.Key, static group => group.Count()),
            Before = Summary(before), After = Summary(after),
            BeforeValueStrings = Summary(before.OfType<BaselineValue>()
                .SelectMany(static value => new[] { value.DisplayValue, value.IntegerValue })
                .Where(static value => value is not null).Cast<object>()
                .Distinct(ReferenceEqualityComparer.Instance).ToArray()),
            BaselineValueX64AlignedBytes = baselineSize,
            BaselineValueObjectBytesEstimate = baselineSize * 5408,
            ManagedItemDeclarationX64AlignedBytesEstimate = 72,
            AfterItemDeclarationObjectBytesEstimate = 72 * 4,
            Exclusions = "Interned static strings are excluded by reference identity; shared dynamic metadata counted once. Containers and other objects are counted by type but not included in value-object byte subtotal. Native current allocation, runtime/static catalogs, transient read projections and history are excluded."
        };
        var output = Environment.GetEnvironmentVariable("RM_CURRENT_BYTES_OUTPUT");
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Kind(uint kind) => kind switch
    { 1 => "float64", 2 => "signed64", 3 => "unsigned64", 4 => "unsigned128", 5 => "bytes", 6 => "boolean", _ => throw new InvalidOperationException() };

    private static BaselineValue Baseline(MetricDeviceFieldBinding field, int round)
    {
        var kind = field.Field.ValueKind;
        var integer = kind is "signed64" or "unsigned64" or "unsigned128"
            ? (123456789UL + (ulong)round).ToString(CultureInfo.InvariantCulture) : null;
        // Matches HEAD projection's canonical integer/bytes/bool empty display;
        // ordinary float and signed64 use the default unit's 0.## presentation.
        var display = kind is "float64" or "signed64" ? (42.125 + round).ToString("0.##", CultureInfo.InvariantCulture) : "";
        return new(field.RequestAddress, field.Field.Name, "recorded", display, 42.125 + round,
            field.Field.Unit, null, field.SourceId)
        {
            FieldName = field.Field.Name, DeviceIdentity = field.DeviceIdentity,
            IntegerValue = integer, ValueKind = kind, SourceId = field.SourceId,
            SampleDurationMilliseconds = 1000
        };
    }

    private static HardwareMetricSnapshot Frame(int round, Dictionary<string, MetricDeviceFieldBinding> bindings)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(round);
        var items = bindings.Values.ToDictionary(static field => field.RequestAddress, field => new MetricReadingValue(
            field.RequestAddress, 42.125 + round, field.Field.Unit)
        {
            FieldName = field.Field.Name, DeviceIdentity = field.DeviceIdentity, ValueKind = field.Field.ValueKind,
            SourceId = field.SourceId, ObservedAt = at, SampleDurationMilliseconds = 1000,
            IntegerValue = field.Field.ValueKind is "signed64" or "unsigned64" or "unsigned128" ? new(123456789UL + (ulong)round, 0) : null
        });
        var provider = new HardwareSensorProviderState("recorded", "current", null);
        var gpu = new GpuMetrics(0, "Recorded GPU", round, 100, 200, 50, 300, 400, 800, 50,
            new(provider, null, null, null, null, null, null, null, null, null, null), "recorded", true, "recorded-gpu");
        return new(at, new("Recorded CPU", round, true, CpuMetricObservationStatus.Complete,
                (ulong)round, 1000, 2000, 4000, 50, "recorded", new(provider, null, null, null, null)),
            new(400, 800, 50, true), new(500, 1000, 50, true), [gpu],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 1, []), items)
        {
            FieldBindings = bindings, WorkspaceIdentity = 1, ConfigurationGeneration = 1,
            CatalogGeneration = 1, CommittedGeneration = (ulong)round,
            Datasets = DatasetIds.ToDictionary(static id => id, id => new HardwareMetricDatasetObservation(
                id, SamplingObservationStatus.Current, (ulong)round, at.UtcTicks, 1, 1, 1, (ulong)round))
        };
    }

    private static object Summary(object[] values)
    {
        var strings = values.OfType<string>().Where(static value => !ReferenceEquals(string.IsInterned(value), value)).ToArray();
        return new
        {
            ReferenceObjectsByType = values.Where(static value => !value.GetType().IsValueType && value is not string)
                .GroupBy(static value => value.GetType().FullName!).ToDictionary(static group => group.Key, static group => group.Count()),
            NonStaticStringObjects = strings.Length,
            StringUtf16PayloadBytes = strings.Sum(static value => (long)value.Length * 2),
            StringX64AlignedObjectBytesEstimate = strings.Sum(static value => ((22L + 2L * value.Length + 7) / 8) * 8)
        };
    }

    private static object[] Graph(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>(); queue.Enqueue(root);
        var iterations = 0;
        while (queue.TryDequeue(out var value))
        {
            Assert.True(++iterations < 500000, "Bounded current accounting traversal exceeded 500,000 visits.");
            var type = value.GetType();
            if (!type.IsValueType && !seen.Add(value)) continue;
            if (value is string or Type || type.IsPrimitive || type.IsEnum || value is IntPtr or UIntPtr) continue;
            if (value is Delegate callback) { if (callback.Target is { } target) queue.Enqueue(target); continue; }
            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable) if (item is not null) queue.Enqueue(item);
                continue;
            }
            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && !field.FieldType.IsPointer && field.GetValue(value) is { } child)
                        queue.Enqueue(child);
        }
        return seen.ToArray();
    }

    private static long AllocationSize(Func<BaselineValue> create)
    {
        // Estimate layout from 10,000 isolated record clones; strings reused and
        // factory warmed before measuring. Runtime allocation is 8-byte aligned.
        var source = create();
        for (var i = 0; i < 100; i++) GC.KeepAlive(source with { });
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) GC.KeepAlive(source with { });
        return (GC.GetAllocatedBytesForCurrentThread() - start) / 10000;
    }

    private sealed record BaselineCapture(Dictionary<string, BaselineValue> Items,
        Dictionary<string, MetricDeviceFieldBinding> FieldBindings);

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }

    // HEAD src/ReadWriteCore/Fields/MetricValue.cs field-for-field layout.
    private sealed record BaselineValue(string Id, string Label, string Group, string DisplayValue,
        double? NumericValue, string Unit, double? Percent, string? Detail, double? Total = null)
    {
        public string? FieldName { get; init; }
        public string? DeviceIdentity { get; init; }
        public string? IntegerValue { get; init; }
        public byte[]? BytesValue { get; init; }
        public bool? BooleanValue { get; init; }
        public string? ValueKind { get; init; }
        public string? SourceId { get; init; }
        public ulong? SampleDurationMilliseconds { get; init; }
    }
}
