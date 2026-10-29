using System.Collections.Immutable;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class CpuResidencyOwnerConsumer
{
    private const string Name = "process_all_cpu_residency_attributed";
    // Public recorded projection and passive borrowing only. Does not claim a replayed native CSwitch transport.
    internal static void Run()
    {
        var field = ProcessMetricFieldRegistry.Find(Name) ?? throw new InvalidDataException("Residency definition missing.");
        if (field.ValueKind != "float64" || field.Unit != "%" || field.ReadMask != ProcessMetricReadMask.None
            || field.DatasetId != ProcessMetricDatasetIds.ObservedCpuResidency) throw new InvalidDataException("Residency contract changed.");
        var at = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var scopes = new CpuLogicalProcessorResidencyScope[] { new(0, "core:0", null, "recorded OS core map; CCD unavailable"), new(1, "core:1", null, "recorded OS core map; CCD unavailable") };
        var inventory = new ProcessMetricFieldSnapshot(at, null, [], [new(41, "100", [])]);
        var window = new CpuCoreResidencySnapshot(at.AddSeconds(9), TimeSpan.FromSeconds(1), 37, at.AddSeconds(-1), at,
            [new("37:1", 41, "100", "recorded", 1000, 2, 1, null, "core:1", 1, [], [],
                [new(0, "core:0", "", 250, 1, 25), new(1, "core:1", "", 750, 1, 75)], [])]);
        var selected = WindowsCpuResidencyProcessFields.Project([Name], inventory, scopes, window);
        if (selected.FindProcess(41, "100", logicalProcessor: scopes[0])!.Values.Single().NumericValue != 25
            || selected.FindProcess(41, "100", logicalProcessor: scopes[1])!.Values.Single().NumericValue != 75
            || selected.CapturedAt != at || selected.Sources.Single().WindowMilliseconds != 1000)
            throw new InvalidDataException("Attributed shares, logical scope or actual window boundary collapsed.");
        var reused = WindowsCpuResidencyProcessFields.Project([Name], inventory with { Processes = [new(41, "200", [])] }, scopes, window);
        if (reused.Processes.Any(row => row.Values.Single().NumericValue is not null)) throw new InvalidDataException("PID reuse rebound old execution.");
        var noDenominator = WindowsCpuResidencyProcessFields.Project([Name], inventory, scopes, window with { Processes = [] });
        if (noDenominator.Processes.Any(row => row.Values.Single().NumericValue is not null)) throw new InvalidDataException("Missing attribution became zero.");
        var broker = new PassiveBroker();
        using var owner = WindowsProcessFieldOwner.CreateDefault(broker);
        using var borrow = owner.BorrowSubscriptionCapture();
        if (owner.ReadCachedSnapshot() is not null || owner.Backend.Catalog.Fields.Length != 0 || broker.Acquires != 0)
            throw new InvalidDataException("Construction/cached read/borrow started native collection.");
        if (typeof(CpuCoreResidencySnapshot).Assembly != typeof(WindowsProcessFieldOwner).Assembly
            || typeof(EtwCpuResidencyCaptureOwner).Assembly != typeof(WindowsProcessFieldOwner).Assembly)
            throw new InvalidDataException("Real residency owner remains outside the SDK.");
        Console.WriteLine("CPU residency recorded canonical consumer passed: typed logical scope, PID/FILETIME, attributed denominator, measured boundary, unavailable and passive borrowing; native window transport not claimed.");
    }

    // Explicit opt-in, real default production SDK route. Merely wiring this method does not enable ETW.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RESOURCE_MANAGER_RUN_LIVE_ETW_TESTS") != "1")
        { Console.WriteLine("CPU residency real Windows ETW consumer not requested; no native availability claimed."); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        await owner.RunCpuResidencyCaptureAsync(async _ =>
        {
            await owner.CollectAsync([Name], timeout.Token); // selected-only: no thread-count snapshot, network or disk lease
            var fields = owner.Backend.Catalog.Fields.Where(field => field.Definition.Name == Name
                && field.Field.DeviceIdentity.StartsWith($"windows-process|{Environment.ProcessId}:", StringComparison.Ordinal))
                .Select(field => field.Field).ToImmutableArray();
            if (fields.IsEmpty) { Console.WriteLine("CPU residency actual ETW/topology unavailable; complete execution not claimed."); return; }
            await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
            owner.ReconcileOwnedEtw();
            var current = await owner.Backend.CollectAsync(fields, timeout.Token);
            foreach (var reading in current)
            {
                if (owner.Backend.ReadCached(reading.Field) != reading) throw new InvalidDataException("Default canonical current differs.");
                if (reading.Value is not null && (reading.SourceId != "windows.etw.cpu-residency" || reading.SampleDurationMilliseconds != 5000))
                    throw new InvalidDataException("Actual residency observation omitted its closed window.");
            }
            Console.WriteLine($"CPU residency actual default Windows canonical consumer completed: selected={current.Length}, current={current.Count(reading => reading.Status == FieldObservationStatus.Current)}; unavailable entries are not inferred zeros.");
        }, timeout.Token);
    }

    private sealed class PassiveBroker : IKernelEtwSessionBroker
    {
        public int Acquires;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        { Acquires++; throw new InvalidOperationException("Recorded consumer must not start ETW."); }
        public KernelEtwSessionSnapshot GetSnapshot() => new("NotRequested", "recorded", 0, DateTimeOffset.UnixEpoch, 0, 0, 0);
    }
}
