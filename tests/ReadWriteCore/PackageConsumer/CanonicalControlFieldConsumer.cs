using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
internal static class CanonicalControlFieldConsumer
{
    // Package-only executable seam: RunAsync is called by the existing consumer
    // entry point during acceptance. No product assembly or internal reflection.
    internal static async Task RunAsync()
    {
        Require(WindowsMetricControlBindings.All.Count == 30 && WindowsMetricControlBindings.All.Count(entry => entry.Implemented) == 25,
            "Managed 30/25/5 contract table drifted.");
        using (var recording = new Fixture())
        {
            using (var actual = WindowsHardwareMetricOwner.CreateDefault(new() { Controls = new(recording.Folder) }))
                Require(!Directory.Exists(recording.Folder), "Actual default construction created a baseline or started a control operation.");
            using var owner = recording.Owner(); using var client = new HttpClient();
            var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
            Require(recording.Opens == 0 && core.Catalog.Fields.IsEmpty, "Catalog opened control hardware.");
            var catalog = await core.DiscoverAsync();
            var power = catalog.Fields.Single(capability => capability.Field.FieldName == "cpu_intel_requested_pl1_power_limit_watts").Field;
            Require(recording.Opens == 1 && recording.Intel.Writes == 0, "Explicit discovery did not use the original lazy Probe.");
            var reads = recording.Intel.Reads;
            Require(core.Query(power).Write.Supported && core.Query(power).WriteContract is not null, "Managed write metadata was not public.");
            Require(core.ReadCached(power).Value is null && recording.Intel.Reads == reads, "Query/cache performed control reads.");
            Require((await core.CollectAsync([power])).Single().Value?.Number == 45, "Configured PL1 read did not reach original current.");
            Require((await core.WriteAsync(power, new(Number: 45.5))).Status == "failed" && recording.Intel.Writes == 0,
                "Step rejection reached setter.");
            var before = owner.ReadCachedSnapshot(); var applied = await core.WriteAsync(power, new(Number: 40));
            Require(applied.Status == "applied" && applied.Evidence?.Kind == FieldWriteEvidence.MaskedRegisterReadback,
                "Original verified write lost evidence.");
            Require(MetricSnapshotFactAssertions.SameCurrent(before, owner.ReadCachedSnapshot()) && core.ReadCached(power).Value?.Number == 45,
                "Write fabricated current before Collect.");
            Require((await core.CollectAsync([power])).Single().Value?.Number == 40, "Explicit Collect did not publish new encoded setting.");
            recording.Topology = Fixture.SingleTopology(recording.Identity) with { Packages = [new("package:0", [0]), new("package:1", [1])] };
            var settings = recording.Intel.Writes;
            Require((await core.WriteAsync(power, new(Number: 50))).Status == "failed" && recording.Intel.Writes == settings,
                "Ambiguous CPU redirected original setter.");
            Require((await core.CollectAsync([power])).Single().Value is null && core.ReadCached(power).Value is null,
                "Unavailable exact CPU retained an old numeric current.");
        }
        using (var recording = new Fixture("AuthenticAMD"))
        {
            using var owner = recording.Owner(); using var client = new HttpClient();
            var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
            var catalog = await core.DiscoverAsync();
            var power = catalog.Fields.Single(capability => capability.Field.FieldName == "cpu_amd_requested_stapm_power_limit_watts").Field;
            var pbo = catalog.Fields.Single(capability => capability.Field.FieldName == "cpu_amd_requested_pbo_scalar_multiplier").Field;
            Require((await core.CollectAsync([power])).Single().Value?.Number == 45 && Helper.ActualPower == 7,
                "Configured AMD limit was replaced with actual power.");
            var accepted = await core.WriteAsync(pbo, new(Number: 2));
            Require(accepted.Status == "applied" && accepted.Evidence?.Kind == FieldWriteEvidence.AcceptedOnly
                && accepted.Evidence!.ObservedValue is null, "PBO acceptance fabricated verification/readback.");
            Require((await core.CollectAsync([pbo])).Single().Value is null, "PBO default fabricated numeric current.");
        }
        Console.WriteLine("Canonical control fields passed recorded original SDK owner/current/Probe/encoded-readback/AMD-limit/accepted-only/identity contracts; actual factory only constructed, no driver/helper or physical setter executed.");
    }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    // Only original lower reader/transport seams are recorded. The producer,
    // native workspace, capture, publication/current and raw baseline algorithms remain real.
    private sealed class Fixture : IDisposable
    {
        internal readonly string Folder = Path.Combine(Path.GetTempPath(), "rm-canonical-controls-" + Guid.NewGuid().ToString("N"));
        internal readonly IntelHardware Intel = new();
        internal readonly Helper Bridge = new();
        internal readonly AdlxHardware Adlx = new();
        internal CpuIdentityInformation Identity;
        internal CpuOsTopologyInformation Topology;
        internal WindowsGpuAdapterInventoryRead Inventory = EmptyInventory();
        internal readonly IntelCpuControlResource IntelResource;
        internal readonly AmdCpuControlResource AmdResource;
        internal readonly AmdDesktopGpuControlResource DesktopResource;
        internal readonly IntegratedGpuControlResource IntegratedResource;
        internal int Opens;
        internal Fixture(string vendor = "GenuineIntel")
        {
            Identity = new(vendor, 6, 154, 3, "recorded CPU", [], []);
            Topology = SingleTopology(Identity);
            IntelResource = new(() => { Opens++; return Intel; }, Path.Combine(Folder, "intel.json"));
            AmdResource = new(Bridge, AmdCpuControlOptions.InDirectory(Folder));
            DesktopResource = new(Adlx, new(Path.Combine(Folder, "desktop.json")));
            IntegratedResource = new(Bridge, new IntelGraphicsHardware(), new(Path.Combine(Folder, "integrated.json"), _ => Task.FromResult(true)));
        }
        internal WindowsMetricControlResources Controls => new()
        {
            IntelCpuControl = IntelResource, AmdCpuControl = AmdResource,
            AmdDesktopGpuControl = DesktopResource, IntegratedGpuControl = IntegratedResource,
            ControlReadCpuIdentity = () => Identity, ControlReadCpuTopology = () => Topology,
            ControlIsFixedChassis = () => true
        };
        internal WindowsMetricControlFields Producer() => new(Controls, () => Inventory);
        internal WindowsHardwareMetricOwner Owner() => new(WindowsMetricConfiguration.CreateDefault(), Readers(), new());
        internal static HardwareMetricSnapshot Snapshot() => new(DateTimeOffset.UtcNow, null!, null!, null!, [], null!, new Dictionary<string, MetricReadingValue>())
            { WorkspaceIdentity = 1, ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1 };
        internal static CpuOsTopologyInformation SingleTopology(CpuIdentityInformation identity) => new([new("core:0", 0, false, [0])], [])
        {
            Packages = [new("package:0", [0])],
            ThreadCpuid = [new(0, new(identity, null, null, null, null, null, null, []))]
        };
        internal static WindowsGpuAdapterInventoryRead EmptyInventory() => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []);
        internal static WindowsGpuAdapterInventoryRead GpuInventory(WindowsGpuAdapterKind kind = WindowsGpuAdapterKind.Dedicated,
            string pnp = "PCI\\VEN_1002&DEV_73BF\\RECORDED", int index = 0) => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
            [new(index, "recorded AMD GPU", 0x1002, 0x73BF, 1, new AdapterLuid { LowPart = 1, HighPart = 8 }, false, 2UL * 1024 * 1024 * 1024, kind)
            {
                PhysicalIdentityStatus = SamplingObservationStatus.Current,
                PhysicalIdentities = [new(0, pnp, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x1002, 0x73BF, 0x1002, 1, 1,
                    WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
                    WindowsGpuPhysicalIdentityStatus.Bound, 0)]
            }]);
        // Copy of the existing callback fixture shape; no second sampler or metric projection.
        private WindowsHardwareMetricResources Readers() => new()
        {
            Controls = Controls,
            ReadCpuCounters = () => throw new InvalidOperationException("unselected CPU telemetry"),
            ReadMemory = () => throw new InvalidOperationException("unselected memory telemetry"),
            ReadVirtualMemory = () => throw new InvalidOperationException(),
            ReadNumaAvailable = (_, selected) => selected.Count == 0 ? new Dictionary<string, ulong>() : throw new InvalidOperationException(),
            ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadGpuInventory = () => Inventory,
            ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
            ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
            ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
            ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
            ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadBattery = (_, _) => throw new InvalidOperationException(),
            ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(), ReadSystemIo = _ => throw new InvalidOperationException(),
            ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadStorageSensors = () => throw new InvalidOperationException(), ReadNvmeSensors = _ => throw new InvalidOperationException(),
            ReadAtaSensors = _ => throw new InvalidOperationException(), ReadHardwareMonitor = () => throw new InvalidOperationException(),
            ReadNotebookFans = () => throw new InvalidOperationException(), SynchronizeNotebookDemand = _ => { }
        };
        public void Dispose()
        {
            IntegratedResource.Dispose(); DesktopResource.Dispose(); AmdResource.Dispose(); IntelResource.Dispose(); Bridge.Dispose();
            if (!Directory.Exists(Folder)) return;
            // All fixtures create only immediate baseline files in this unique directory.
            foreach (var file in Directory.EnumerateFiles(Folder)) File.Delete(file);
            Directory.Delete(Folder);
        }
    }
    private sealed class IntelHardware : IIntelCpuHardware
    {
        internal ulong Limits = 360UL | (1UL << 15) | (640UL << 32);
        internal int Reads, Writes;
        internal bool FailReads = false, IgnoreWrites = false;
        public ulong ReadMsr(uint register)
        {
            Reads++; if (FailReads) throw new IOException("recorded unavailable");
            return register switch { 0x606 => 3UL | (10UL << 16), 0x610 => Limits, _ => throw new IOException() };
        }
        public bool CanWriteMsr(uint register) => register == 0x610;
        public void WriteMsr(uint register, ulong value)
        { if (register != 0x610) throw new IOException(); Writes++; if (!IgnoreWrites) Limits = value; }
        public void Dispose() { }
    }
    private sealed class Helper : IHardwareBridgeProcessClient
    {
        public bool IsInstalled => true;
        public string? UnavailableReason => null;
        internal int Calls, Settings;
        internal double Stapm = 45, Slow = 55, Fast = 65, CpuCounts = -5, GpuCounts = 8;
        internal bool OmitGpuWriteReadback = false;
        internal const double ActualPower = 7;
        public HardwareBridgeReadingsSnapshot? ReadCurrentReadings()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            HardwareBridgeReading Reading(double? value, string unit) => new(value, unit,
                value is null ? HardwareBridgeReadingStatus.Missing : HardwareBridgeReadingStatus.Valid, timestamp);
            return new(Reading(Stapm,"W"), Reading(ActualPower,"W"), Reading(Fast,"W"), Reading(Slow,"W"),
                Reading(null,"A"), Reading(null,"A"), Reading(CpuCounts,"档"), Reading(GpuCounts,"档"), Reading(null,"MHz"));
        }
        public IDisposable SubscribeReadings() => new ReadingSubscription();
        private sealed class ReadingSubscription : IDisposable { public void Dispose() { } }
        public Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (operation == "read") throw new InvalidOperationException("Current readings must come from the typed snapshot.");
            if (operation.StartsWith("set-", StringComparison.Ordinal)) Settings++;
            if (operation == "set-power-limit")
            {
                var value = Convert.ToDouble(arguments!["value"]);
                if ((string?)arguments["which"] == "stapm") Stapm = value;
                else if ((string?)arguments["which"] == "slow") Slow = value; else Fast = value;
            }
            if (operation == "set-curve-optimizer") CpuCounts = Convert.ToDouble(arguments!["value"]);
            if (operation == "set-igpu-curve-optimizer")
            {
                GpuCounts = Convert.ToDouble(arguments!["value"]);
                if (OmitGpuWriteReadback) return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true, accepted = true }));
            }
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                ok = true, available = true, powerTable = true, curveOptimizer = true, integratedGpuCurveOptimizer = true,
                accepted = true, status = "OK", stapmLimitWatts = Stapm, slowLimitWatts = Slow, fastLimitWatts = Fast,
                curveOptimizerCounts = CpuCounts, integratedGpuCurveOptimizerCounts = GpuCounts, actualPowerWatts = ActualPower
            }));
        }
        public void Dispose() { }
    }
    private sealed class IntelGraphicsHardware : IIntelGraphicsControlHardware
    {
        public bool IsAvailable => false;
        public IntPtr? FindIntegratedDevice(ulong? key) => null;
        public bool TryAcceptOverclockWaiver(IntPtr device) => throw new InvalidOperationException("unselected waiver");
        public double? ReadFrequencyOffsetMhz(IntPtr device) => throw new InvalidOperationException();
        public int WriteFrequencyOffsetMhz(IntPtr device, double value) => throw new InvalidOperationException("unselected setter");
    }
    private sealed class AdlxHardware : IAdlxTuningHardware
    {
        internal string Pnp = "PCI\\VEN_1002&DEV_73BF\\RECORDED";
        internal AdlxGfxMode Mode = AdlxGfxMode.Absolute;
        internal int Reads, Writes;
        internal readonly Dictionary<int, int> Values = new() { [0] = 0, [1] = 600, [2] = 2400, [3] = 1000, [4] = 2000, [5] = 500, [6] = 1500, [7] = 1, [8] = 0, [9] = 0 };
        public AdlxTuningValue Read(int? adapterIndex, int field)
        {
            Reads++;
            var (min, max, step) = field switch { 0 => (-10, 14, 2), 7 => (0, 1, 1), 8 or 9 => (-500, 500, 5), _ => (0, 4000, 1) };
            // Return actual mode metadata, allowing the producer itself to refuse
            // incompatible absolute/offset declarations before the recorded setter.
            return new(Pnp, Values[field], min, max, step, Mode);
        }
        public void Write(string pnp, int field, int value)
        { if (pnp != Pnp) throw new IOException(); Writes++; Values[field] = value; }
    }
    private static MetricDeviceFieldBinding Binding(HardwareMetricSnapshot snapshot, string name)
        => snapshot.FieldBindings.Values.Single(binding => binding.Field.Name == name);
    private static FieldReference Reference(MetricDeviceFieldBinding binding) => new(binding.DeviceIdentity, binding.Field.Name);
}
