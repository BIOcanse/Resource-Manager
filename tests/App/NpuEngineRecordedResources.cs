using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

internal sealed class NpuEngineRecordedResources
{
    internal const string Raw = "npu_all_engine_running_time_microseconds";
    internal const string Ratio = "npu_all_engine_reported_running_time_ratio_percent";
    internal const string Busy = "npu_all_engine_busy_percent";
    internal int Count = 2, Opens, Reads, UnselectedReads;
    internal bool Fail, Unavailable, InvalidateDuringRead;
    internal ulong Counter = 9_007_199_254_740_993, Ticks = 1_000_000, Frequency = 1_000_000;
    internal ulong CounterStep = 1_250_000, TickStep = 1_000_000;
    internal uint? MissingEngine;
    internal readonly Dictionary<uint, ulong> EngineCounterSteps = [];
    internal readonly Dictionary<uint, ulong> EngineTickOffsets = [];
    private readonly Dictionary<uint, ulong> engineCounters = [];
    internal Action? AfterRead;
    internal Func<MemoryMetrics>? ReadMemory;
    internal readonly List<Directory> Opened = [];
    internal IReadOnlySet<string> LastRequested = new HashSet<string>();
    internal uint LastMaximum;
    internal NpuEngineTopologyRead Open(uint maximum, IWindowsNpuEngineDirectory? prior)
        => Unavailable ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [])
            : WindowsNpuEngineTopology.OpenDirectory(bound =>
            {
                Opens++;
                if (Count > bound) return null;
                var directory = new Directory(Count); Opened.Add(directory); return directory;
            }, maximum, prior);
    internal IReadOnlyList<NpuEngineRead> Read(IReadOnlyList<NpuEngineCatalogIdentity> devices,
        IReadOnlySet<string> requested, uint maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Reads++; LastRequested = requested; LastMaximum = maximum;
        var ticks = Ticks;
        Counter = checked(Counter + CounterStep); Ticks = checked(Ticks + TickStep);
        var rows = devices.Where(engine => requested.Contains(engine.ExactIdentityKey) && engine.EngineIndex != MissingEngine)
            .Select(engine =>
            {
                var counter = engineCounters.TryGetValue(engine.EngineIndex, out var current) ? current : Counter - CounterStep;
                var step = EngineCounterSteps.GetValueOrDefault(engine.EngineIndex, CounterStep);
                engineCounters[engine.EngineIndex] = checked(counter + step);
                var queryTicks = checked(ticks + EngineTickOffsets.GetValueOrDefault(engine.EngineIndex));
                return new NpuEngineRead(engine, Fail ? null : new NpuEngineSample(counter, queryTicks - 1, queryTicks, Frequency), InvalidateDuringRead);
            }).ToArray();
        if (InvalidateDuringRead) foreach (var directory in Opened) directory.Current = false;
        AfterRead?.Invoke(); return rows;
    }
    internal void ResetCounters(ulong value)
    { Counter = value; engineCounters.Clear(); }
    internal void AdvanceCounters(ulong value)
    {
        Counter = checked(Counter + value);
        foreach (var engine in engineCounters.Keys.ToArray()) engineCounters[engine] = checked(engineCounters[engine] + value);
    }
    internal WindowsHardwareMetricResources Create() => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded",
        Controls = new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation([], []) },
        OpenNpuEngineDirectory = Open, ReadNpuEngines = Read,
        ReadNpuMemoryIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
            [new NpuMemoryCatalogIdentity(@"ACPI\RECORDED_ENGINE_NPU\ONE")]),
        ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
        ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNvmlFanIdentities = static _ => [],
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => ReadMemory?.Invoke() ?? throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(), ReadCpuFrequency = () => throw Unexpected(),
        ReadGpuUsage = _ => PdhGpuEngineUsageRead.NotRequested,
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(), ReadAdlxAsync = (_, _, _) => throw Unexpected(),
        ReadAmdSmu = _ => throw Unexpected(), ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBattery = (_, _) => throw Unexpected(), ReadNetworkInterfaces = (_, _) => throw Unexpected(), ReadSystemIo = _ => throw Unexpected(),
        ReadStorageSensors = () => throw Unexpected(), ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(),
        ReadHardwareMonitor = () => throw Unexpected(), ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { }
    };
    private Exception Unexpected() { UnselectedReads++; return new InvalidOperationException("Unselected engine fixture source."); }

    internal sealed class Directory : IWindowsNpuEngineDirectory
    {
        private readonly object gate = new();
        private int references = 1, initialReleased;
        internal bool Current = true;
        internal bool Disposed;
        internal int AcquireCalls;
        internal Directory(int count)
        {
            var id = Guid.NewGuid();
            Identities = Enumerable.Range(0, count).Select(index => new NpuEngineCatalogIdentity(
                @"ACPI\RECORDED_ENGINE_NPU\ONE", checked((uint)index), "Same name", id, 7, 0x8086, 0x1234,
                checked((uint)count), 1, "1", this)).ToArray();
        }
        public IReadOnlyList<NpuEngineCatalogIdentity> Identities { get; }
        public bool IsCurrent(uint maximum, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) return !Disposed && Current && maximum > 0 && Identities.Count <= maximum; }
        public IDisposable AcquireLease()
        {
            lock (gate) { ObjectDisposedException.ThrowIf(Disposed, this); AcquireCalls++; references++; return new Borrow(this); }
        }
        public void Dispose() { if (Interlocked.Exchange(ref initialReleased, 1) == 0) Release(); }
        private void Release() { lock (gate) if (--references == 0) Disposed = true; }
        private sealed class Borrow(Directory directory) : IDisposable
        {
            private Directory? owner = directory;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
        }
    }
}
