using ResourceManager.App.Domain.Metrics;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class InformationProcessConsumer
{
    internal static void Run()
    {
        var observed = DateTimeOffset.UtcNow;
        var definitions = typeof(DeviceInformationFieldRegistry).GetFields(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Static).Select(field => field.GetValue(null)).OfType<DeviceInformationDefinition>().ToArray();
        Require(definitions.Length > 100 && definitions.Select(field => field.Name).Distinct().Count() == definitions.Length,
            "complete independent original information definitions");
        foreach (var definition in definitions)
        {
            var descriptor = InformationFieldProjection.Describe(definition);
            Require(descriptor.Name == definition.Name && descriptor.Unit == definition.Unit,
                "all declared information kinds have explicit metadata projection");
        }
        var integer = new DeviceInformationField(DeviceInformationFieldRegistry.StorageAllNvmeControllerAccessibleTotalNvmCapacityReportedBytes,
            JsonSerializer.SerializeToElement(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture)));
        var reading = InformationFieldProjection.Read("recorded-controller", integer, observed);
        Require(reading.Value?.Integer == MetricInteger.FromUnsigned128(UInt128.MaxValue), "information UInt128 exact");
        Require(reading.SourceId == integer.Definition.SourceId && reading.ObservedAt == observed,
            "information source and time retained");
        Require(InformationFieldProjection.Describe(integer.Definition).ValueKind == "unsigned128", "typed information descriptor");

        var zero = integer with { Value = JsonSerializer.SerializeToElement("0") };
        Require(InformationFieldProjection.Read("recorded-controller", zero, observed).Value?.Integer == MetricInteger.Parse("0"), "valid integer zero");
        var absent = integer with { Value = null };
        Require(InformationFieldProjection.Read("recorded-controller", absent, observed) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "failed information is empty");
        var invalid = integer with { Value = JsonSerializer.SerializeToElement("-1") };
        Require(InformationFieldProjection.Read("recorded-controller", invalid, observed) is { Status: FieldObservationStatus.Invalid, Value: null }, "invalid unsigned integer is empty");

        var boolean = new DeviceInformationField(DeviceInformationFieldRegistry.CpuAllSmtEnabled, JsonSerializer.SerializeToElement(false));
        Require(InformationFieldProjection.Read("recorded-cpu", boolean, observed).Value?.Boolean == false, "valid false");
        var emptyText = new DeviceInformationField(DeviceInformationFieldRegistry.MachineAllModelDeclared, JsonSerializer.SerializeToElement(""));
        Require(InformationFieldProjection.Read("machine", emptyText, observed).Value?.Text == "", "valid empty text");
        var raw = new DeviceInformationField(DeviceInformationFieldRegistry.DisplayAllEdidRawDeclared,
            JsonSerializer.SerializeToElement(Convert.ToBase64String([0, 255, 1])));
        Require(InformationFieldProjection.Read("recorded-display", raw, observed).Value?.Bytes is { } bytes
            && bytes.SequenceEqual(new byte[] { 0, 255, 1 }), "raw bytes preserve original payload");
        var emptyRaw = raw with { Value = JsonSerializer.SerializeToElement("") };
        Require(InformationFieldProjection.Read("recorded-display", emptyRaw, observed).Value?.Bytes is { IsEmpty: true }, "valid empty raw bytes");
        FieldReading structure;
        using (var document = JsonDocument.Parse("[{\"group\":1,\"index\":63}]"))
            structure = InformationFieldProjection.Read("recorded-cpu",
                new(DeviceInformationFieldRegistry.CpuAllNumaNodeCpuAffinity, document.RootElement), observed);
        Require(structure.Value?.Structure is { } tree && tree[0].GetProperty("index").GetInt32() == 63,
            "structured value owns valid data after source document disposal");

        var snapshot = new DeviceInformationSnapshot(observed, [new("recorded-controller", "physical_disk", [integer,
            absent with { Definition = DeviceInformationFieldRegistry.StorageAllNvmeControllerAccessibleUnallocatedNvmCapacityReportedBytes }])]);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<DeviceInformationSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(MetricInteger.Parse(restored.Devices[0].Fields[0].Value!.Value.GetString()!) == reading.Value!.Integer
            && restored.Devices[0].Fields[1].Value is null, "independent original information wire contract");

        var io = new ProcessIoObservation(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0, 1, ulong.MaxValue);
        var recorded = new ProcessBasicMetricObservation(97_500_000,
            new(9_007_199_254_740_993, ulong.MaxValue, ulong.MaxValue, 0), io, 0);
        var count = ProcessMetricFieldProjection.CreateBasicValue(Find(ProcessMetricFieldCode.IoOperations), recorded);
        Require(count.IntegerValue == MetricInteger.Parse("55340232221128654845"), "process exact cumulative UInt128 sum");
        Require(ProcessMetricFieldProjection.CreateBasicValue(Find(ProcessMetricFieldCode.WorkingSet), recorded).IntegerValue == MetricInteger.Parse("9007199254740993"), "process exact resident bytes");
        Require(ProcessMetricFieldProjection.CreateBasicValue(Find(ProcessMetricFieldCode.CpuTime), recorded).NumericValue == 9.75, "process cumulative seconds");
        var network = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic).ToArray();
        var basic = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics
            && (int)field.Code >= (int)ProcessMetricFieldCode.CpuTime && (int)field.Code <= (int)ProcessMetricFieldCode.HandleCount).ToArray();
        var query = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics
            && field.SourceId == "windows.process.query").ToArray();
        Require(network.Length == 4 && basic.Length == 10 && query.Length == 19 && ProcessMetricFieldRegistry.Definitions.Count == 34
            && ProcessMetricFieldRegistry.Find("process_all_cpu_residency_attributed")?.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency
            && ProcessMetricFieldRegistry.Find("process_all_gpu_residency_attributed")?.DatasetId == ProcessMetricDatasetIds.GpuResidency,
            "original distinct process sources and canonical field names");
        foreach (var code in new[] { ProcessMetricFieldCode.IoReadOperations, ProcessMetricFieldCode.IoWriteOperations, ProcessMetricFieldCode.IoOtherOperations })
            Require(ProcessMetricFieldProjection.CreateBasicValue(Find(code), recorded).IntegerValue == MetricInteger.FromUnsigned128(ulong.MaxValue), "individual operation counters stay unsigned64 exact");
        var ioRates = ProcessMetricFieldRegistry.Definitions.Where(field => field.SourceId == "windows.process.io-interval").ToArray();
        Require(ioRates.Length == 6 && ioRates.All(field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics && field.ReadMask == ProcessMetricReadMask.Io)
            && ioRates.Count(field => field.Unit == "ops/s") == 3 && ioRates.Count(field => field.Unit == "B/s") == 3, "all direction rate definitions share original process dataset/mask");
        var exactInterval = new ProcessIoInterval(9_007_199_254_740_993, 9_007_199_254_742_993, 1000);
        var intervalJson = JsonSerializer.Serialize(exactInterval, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(JsonSerializer.Deserialize<ProcessIoInterval>(intervalJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) == exactInterval,
            "typed IO interval preserves QPC integers beyond double precision");
        Require(exactInterval.WindowMilliseconds == 2000, "per-process query-return interval duration");
        var processSnapshot = new ProcessMetricFieldSnapshot(observed, null, basic,
            [new(17, "123", basic.Select(field => ProcessMetricFieldProjection.CreateBasicValue(field, recorded)).ToArray())])
            { Sources = [new("windows.process.query", observed, null)] };
        var cleared = processSnapshot.Clear();
        Require(cleared.Processes[0].CreationWindowsFileTime100Ns == "123"
            && cleared.Processes[0].Values.All(value => value.IntegerValue is null && value.NumericValue is null),
            "process failed empty retains original creation identity");

        if (OperatingSystem.IsWindows())
        {
            using var current = Process.GetCurrentProcess();
            var birth = current.StartTime.ToUniversalTime().ToFileTimeUtc();
            var reader = new WindowsProcessBasicMetricReader();
            var actual = reader.Read(current.Id, birth, ProcessMetricReadMask.CpuTime | ProcessMetricReadMask.Memory
                | ProcessMetricReadMask.Io | ProcessMetricReadMask.HandleCount);
            Require(actual.CpuTime100Ns.HasValue && actual.Memory is { WorkingSet: > 0 } && actual.Io is not null && actual.HandleCount > 0,
                "actual independent identity-verified Windows process counters");
            var wrongIdentity = reader.Read(current.Id, birth + 1, ProcessMetricReadMask.Memory);
            Require(wrongIdentity.Memory is null && wrongIdentity.CpuTime100Ns is null && wrongIdentity.Io is null
                && wrongIdentity.HandleCount is null, "PID reuse cannot return another process reading");
            var noDemand = reader.Read(current.Id, birth, ProcessMetricReadMask.None);
            Require(noDemand.Memory is null && noDemand.CpuTime100Ns is null, "no implicit counter categories");
        }
        Console.WriteLine("ReadWriteCore information/process package consumer passed: original models, typed information, exact integers, original failure semantics and actual identity-verified Windows process read.");
    }

    private static ProcessMetricFieldDefinition Find(ProcessMetricFieldCode code)
        => ProcessMetricFieldRegistry.Definitions.Single(field => field.Code == code);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
