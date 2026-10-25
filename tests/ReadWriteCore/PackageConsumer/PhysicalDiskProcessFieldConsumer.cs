using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

internal static class PhysicalDiskProcessFieldConsumer
{
    // Recorded input through actual package public types. No ETW session or native operation.
    internal static void Run()
    {
        const string read = "process_all_physical_disk_read_bytes_attributed";
        const string write = "process_all_physical_disk_write_bytes_attributed";
        var definitions = new[] { ProcessMetricFieldRegistry.Find(read), ProcessMetricFieldRegistry.Find(write) };
        if (definitions.Any(field => field is null || field.ValueKind != "unsigned64" || field.Unit != "B"
            || field.DatasetId != ProcessMetricDatasetIds.ObservedPhysicalDiskIo)) throw new InvalidDataException("Matrix129 declaration missing.");
        var disk0 = new PhysicalDiskIoScope(37, 0); var disk2 = new PhysicalDiskIoScope(37, 2);
        var inventory = new ProcessMetricFieldSnapshot(DateTimeOffset.UnixEpoch, null, [], [new(41, "100", [])]);
        var window = new PhysicalDiskProcessIoWindow(DateTimeOffset.UnixEpoch, 2000,
            [new(disk0, 41, "100", "9007199254740993", "0"), new(disk2, 41, "100", "0", "23")]) { Disks = [disk0, disk2] };
        var exact = WindowsPhysicalDiskProcessFields.Project([read, write], inventory, window);
        if (exact.FindProcess(41, "100", disk0)?.Values.Single(value => value.Field == read).IntegerValue != MetricInteger.Parse("9007199254740993")
            || exact.FindProcess(41, "100", disk2)?.Values.Single(value => value.Field == write).IntegerValue != MetricInteger.Parse("23"))
            throw new InvalidDataException("Disk scope or exact count collapsed.");
        var warming = WindowsPhysicalDiskProcessFields.Project([read, write], inventory, window with { Complete = false });
        if (warming.CapturedAt is not null || warming.Processes.Any(row => row.Values.Any(value => value.IntegerValue is not null)))
            throw new InvalidDataException("Incomplete window was published as zero/current.");
        var reused = WindowsPhysicalDiskProcessFields.Project([read, write], inventory with { Processes = [new(41, "200", [])] }, window);
        if (reused.Processes.Any(row => row.Values.Any(value => value.IntegerValue is not null)))
            throw new InvalidDataException("Old creation was reassigned.");
        var broker = new NoCollectionBroker();
        using var owner = WindowsProcessFieldOwner.CreateDefault(broker);
        using var borrow = owner.BorrowSubscriptionCapture();
        if (owner.ReadCachedSnapshot() is not null || owner.Backend.Catalog.Fields.Length != 0 || broker.Acquires != 0)
            throw new InvalidDataException("Construction or cached directory started collection.");
        if (typeof(EtwPhysicalDiskIoCaptureOwner).Assembly != typeof(WindowsProcessFieldOwner).Assembly)
            throw new InvalidDataException("Shared DiskIO resource remains outside the package.");
        Console.WriteLine("Recorded matrix129 package surface: exact bytes, OS disk report scope, creation identity, warming and passive typed borrowing verified; stable Storage identity and real ETW availability not claimed.");
    }
    private sealed class NoCollectionBroker : IKernelEtwSessionBroker
    {
        internal int Acquires;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        { Acquires++; throw new InvalidOperationException("Recorded consumer must not start ETW."); }
        public KernelEtwSessionSnapshot GetSnapshot() => new("NotRequested", "recorded", 0, DateTimeOffset.UnixEpoch, 0, 0, 0);
    }
}
