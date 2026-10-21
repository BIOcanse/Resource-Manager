using System.Buffers.Binary;
using ResourceManager.ReadWrite.Windows.Cpu;

static class WindowsCpuOsTopologyConsumer
{
    public static void Run()
    {
        Require(typeof(WindowsCpuOsTopologyReader).Assembly == typeof(ResourceManager.ReadWrite.ReadWriteCore).Assembly,
            "independent original OS CPU reader owner");
        var coreBytes = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(coreBytes.AsSpan(4), coreBytes.Length);
        coreBytes[8] = 1; coreBytes[9] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(coreBytes.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(coreBytes.AsSpan(32), (1ul << 63) | 4);
        BinaryPrimitives.WriteUInt16LittleEndian(coreBytes.AsSpan(40), 2);
        var core = WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(coreBytes).Single();
        Require(core.SmtRelationshipReported && core.EfficiencyClass == 7
            && core.LogicalProcessorIds.SequenceEqual([130,191]), "recorded group addresses and reported SMT");
        Require(WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation([]).Count == 0, "successful empty raw topology");
        try { _ = WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(coreBytes[..47]); throw new Exception("truncation accepted"); }
        catch (InvalidOperationException) { }

        var reader = new WindowsCpuOsTopologyReader();
        var notes = new List<string>();
        var information = reader.ReadInformation(notes: notes);
        Require(information.LogicalProcessorBusyPercent is null, "static capture has no busy samples");
        var first = reader.ReadLogicalProcessorUsage(Environment.ProcessorCount, notes);
        Require(first.All(static value => value is null), "first raw interval is unavailable");
        reader.ResetUsageBaseline();
        Require(reader.ReadLogicalProcessorUsage(Environment.ProcessorCount, notes).All(static value => value is null),
            "reset restarts the original interval baseline");
        var counts = WindowsCpuOsTopologyReader.QueryProcessorTimes();
        Require(counts.Count > 0 && counts.Keys.All(static id => id >= 0), "actual group-scoped raw times");
        var sets = WindowsCpuOsTopologyReader.ReadCpuSetInformation(notes);
        var fields = reader.ReadFields(notes: notes);
        Require(fields.Devices.SelectMany(static device => device.Fields)
            .All(static field => field.Definition.Name.StartsWith("cpu_", StringComparison.Ordinal)), "canonical CPU field names");
        Console.WriteLine($"ReadWriteCore OS CPU consumer passed: cores {information.PhysicalCores?.Count ?? -1}, caches {information.Caches?.Count ?? -1}, packages {information.Packages?.Count ?? -1}, dies {information.Dies?.Count ?? -1}, NUMA {information.NumaNodes?.Count ?? -1}, raw times {counts.Count}, CPU sets {sets?.Count ?? -1}, field objects {fields.Devices.Count}; unavailable sources remain null.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
