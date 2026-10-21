using System.Collections.Immutable;
using System.IO.Compression;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows;

// This package consumer intentionally references no Resource Manager host assembly.
if (args.Contains("--current-backend-recorded-only", StringComparer.Ordinal))
{
    await AmdHardwareTopologyConsumer.RunRecordedAsync();
    await SecurityProductConsumer.RunRecordedAsync();
    await EdidModeConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--security-products-recorded-only", StringComparer.Ordinal))
{
    await SecurityProductConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--amd-hardware-topology-recorded-only", StringComparer.Ordinal))
{
    await AmdHardwareTopologyConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--cpu-edid-d3d-capability-recorded-only", StringComparer.Ordinal))
{
    await CpuInstructionConsumer.RunRecordedAsync();
    await EdidModeConsumer.RunRecordedAsync();
    await D3dCapabilityConsumer.RunAsync();
    return;
}
if (args.Contains("--edid-d3d-capability-recorded-only", StringComparer.Ordinal))
{
    await EdidModeConsumer.RunRecordedAsync();
    await D3dCapabilityConsumer.RunAsync();
    return;
}
if (args.Contains("--edid-mode-recorded-only", StringComparer.Ordinal))
{
    await EdidModeConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--nvidia-drs-recorded-only", StringComparer.Ordinal))
{
    await NvidiaDrsInformationConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--process-io-uvc-recorded-only", StringComparer.Ordinal))
{
    await ProcessFieldOwnerConsumer.RunIoRatesWindowsAsync();
    await UvcFrameIntervalConsumer.RunRecordedAsync();
    Console.WriteLine("[PROCESS-IO-UVC-COMBINATION-PASSED] process IO actual-readonly; UVC recorded; no-live-ETW/camera");
    return;
}
if (args.Contains("--uvc-interval-recorded-only", StringComparer.Ordinal))
{
    await UvcFrameIntervalConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--npu-engine-recorded-only", StringComparer.Ordinal))
{
    await NpuEngineConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--intel-fan-recorded-only", StringComparer.Ordinal))
{
    await IntelFanConsumer.RunRecordedAsync();
    return;
}
// Actual hardware/provider ownership is checked separately in product integration.
if (args.Contains("--npu-memory-recorded-only", StringComparer.Ordinal))
{
    await NpuMemoryConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--psys-recorded-only", StringComparer.Ordinal))
{
    await IntelPsysConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--nvidia-fan-recorded-only", StringComparer.Ordinal))
{
    await NvidiaFanConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--npu-recorded-only", StringComparer.Ordinal))
{
    await NpuInformationConsumer.RunRecordedAsync();
    return;
}
await SecurityProductConsumer.RunRecordedAsync();

await AmdHardwareTopologyConsumer.RunRecordedAsync();
await CpuInstructionConsumer.RunRecordedAsync();
await EdidModeConsumer.RunRecordedAsync();
await NvidiaDrsInformationConsumer.RunRecordedAsync();
await UvcFrameIntervalConsumer.RunRecordedAsync();
await IntelFanConsumer.RunRecordedAsync();
await NpuEngineConsumer.RunRecordedAsync();
await NpuMemoryConsumer.RunRecordedAsync();
await NpuInformationConsumer.RunRecordedAsync();
await NvidiaFanConsumer.RunRecordedAsync();
await IntelPsysConsumer.RunRecordedAsync();
using var client = new HttpClient();
var backend = new Backend();
var core = new ReadWriteCore(backend, new DependencyActions(client, []));
var field = core.Catalog.Fields.Single().Field;
Require(core.Query(field).Read.Supported, "public capability");
Require(core.ReadCached(field).Value?.Integer == "18446744073709551615", "exact cached integer");
Require(backend.Collections == 0, "cache has no collection side effect");
var readings = await core.CollectAsync([field]);
Require(readings.Single().Value?.Integer == "18446744073709551615", "direct collection");
Require(backend.Collections == 1, "one explicit backend collection");
Require((await core.WriteAsync(field, new(Integer: "0"))).Status == "unsupported", "read-only field");
Require(backend.Writes == 0, "read-only field did not invoke writer");
Require(core.Catalog.GetFields("unknown").IsEmpty, "explicit device binding");
InformationProcessConsumer.Run();
await FrameFieldOwnerConsumer.RunAsync();
await WindowsExplicitResultConsumer.RunAsync();
await WindowsExplicitBenchmarkExportConsumer.RunAsync();
await WindowsRawInputSessionConsumer.RunRecordedAsync();
EtwOwnerConsumer.Run();
UwAcpiConsumer.Run();
HardwareMonitorWmiConsumer.Run();
ProcessCpuUsageConsumer.Run();
ProcessIdentityConsumer.Run();
TopologyWmiPciConsumer.RunRecorded();
StorageOwnerConsumer.Run();
await AtaSmartAttributeInformationConsumer.RunRecordedAsync();
await AtaSmartInformationPublicationConsumer.RunAsync();
await DeviceTopologyCaptureOwnerConsumer.RunRecordedAsync();
await PrinterInformationConsumer.RunAsync();
await AudioCodecConsumer.RunAsync();
await D3dCapabilityConsumer.RunAsync();
await GpuApiCapabilityConsumer.RunAsync();
await GpuEngineInformationConsumer.RunRecordedAsync();
await OpenClCapabilityConsumer.RunAsync();
await BluetoothClassicInformationConsumer.RunAsync();
await WpdInformationConsumer.RunAsync();
await UsbSpeedConsumerExample.RunRecordedAsync();
await SoftwareInformationConsumer.RunAsync();
ProcessCommandLineConsumer.Run();
await CanonicalControlFieldConsumer.RunAsync();
await IndexedFileOwnerConsumer.RunAsync();
await WindowsFileLayoutOwnerConsumer.RunAsync();
PhysicalDiskProcessFieldConsumer.Run();
CpuResidencyOwnerConsumer.Run();
await WindowsDumpAnalysisConsumer.RunPassive();
await MftDiskUsageOwnerConsumer.RunAsync();
Console.WriteLine("MFT recorded actual scanner/parser/store consumer passed: five fields, explicit detailed view, roots-only default; no live volume read.");
await ProcessStorageGapConsumer.RunAsync();
await IntelCpuControlOwnerConsumer.RunAsync();
await AmdDesktopGpuControlOwnerConsumer.RunAsync();
await AmdCpuIntegratedGpuControlOwnerConsumer.RunAsync();
await WindowsDiskPerformanceConsumer.RunRecordedAsync();

if (OperatingSystem.IsWindows())
{
    PdhHelperConsumer.RunPdh();
    await ProcessCommandLineConsumer.RunWindowsAsync();
    await ProcessDetailsInformationConsumer.RunWindowsAsync();
    await OperatingSystemUptimeConsumer.RunWindowsAsync();
    await OperatingSystemBuildBootConsumer.RunWindowsAsync();
    await OperatingSystemAcPowerConsumer.RunWindowsAsync();
    await OperatingSystemKeyboardScanCodeRemapConsumer.RunWindowsAsync();
    await WindowsComponentInformationConsumer.RunAsync();
    await OperatingSystemQueryEvidenceConsumer.RunAsync();
    await PlatformDeclarationsConsumer.RunWindowsAsync();
    await OperatingSystemSessionConsumer.RunWindowsAsync();
    await StartupConfigurationConsumer.RunWindowsAsync();
    await ProcessGpuResidencyConsumer.RunWindowsAsync();
    await CpuResidencyOwnerConsumer.RunWindowsAsync();
    await DiagnosticsSourceConsumer.RunWindowsAsync();
    await DriverInformationConsumer.RunWindowsAsync();
    await ServiceInformationConsumer.RunWindowsAsync();
    await ProcessStorageGapConsumer.RunWindowsAsync();
    await DeviceTopologyCaptureOwnerConsumer.RunWindowsAsync();
    ProcessIdentityConsumer.RunWindows();
    await ProcessFieldOwnerConsumer.RunWindowsAsync();
    await ProcessFieldOwnerConsumer.RunIoRatesWindowsAsync();
    await PdhHelperConsumer.RunHelperAsync();
    WindowsInformationConsumer.Run();
    TopologyWmiPciConsumer.RunWindows();
    WindowsStorageConsumer.Run();
    StorageOwnerConsumer.RunWindows();
    WindowsCpuFirmwareConsumer.Run();
    WindowsCpuOsTopologyConsumer.Run();
    await CpuCaptureOwnerConsumer.RunWindowsAsync();
    await WindowsMetricOwnerConsumer.RunWindowsAsync();
    await WindowsMetricWriteOwnerConsumer.RunWindowsAsync();
    WindowsDeviceReaderConsumer.Run();
    WindowsDisplayUsbConsumer.Run();
    await DisplayOwnerConsumer.RunWindowsAsync();
    WindowsGpuVendorConsumer.Run();
    CpuControlConsumer.Run();
    AmdSmuConsumer.Run();
    NativeSnapshotConsumer.Run();
    await MetricWorkspaceOwnerConsumer.RunWindowsAsync();
    var physical = WindowsSystemCounters.ReadPhysicalMemory();
    Require(physical.IsAvailable && physical.TotalBytes > 0 && physical.AvailableBytes <= physical.TotalBytes,
        "actual independent Windows physical memory read");
    var cpuBefore = WindowsSystemCounters.ReadCpuCounters();
    await Task.Delay(20);
    var cpuAfter = WindowsSystemCounters.ReadCpuCounters();
    Require(cpuBefore.IsAvailable && cpuAfter.IsAvailable && cpuAfter.KernelTicks >= cpuAfter.IdleTicks
        && cpuAfter.MonotonicTicks > cpuBefore.MonotonicTicks && cpuAfter.MonotonicTicksPerSecond > 0
        && cpuAfter.KernelTicks >= cpuBefore.KernelTicks && cpuAfter.UserTicks >= cpuBefore.UserTicks,
        "actual independent exact Windows CPU counters");
    Console.WriteLine("ReadWriteCore Windows package consumer passed: actual physical memory and raw CPU counters; no product or subscription dependency.");
}
var publication = new SamplingPublicationSignal();
using (var selectedNotification = publication.Subscribe(["selected-device-field"]))
using (var other = publication.Subscribe(["other-device-field"]))
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    var selectedWait = selectedNotification.WaitAsync(timeout.Token).AsTask();
    publication.Publish(["other-device-field"]);
    Require(!selectedWait.IsCompleted, "only selected publication wakes its consumer");
    await other.WaitAsync(timeout.Token);
    publication.Publish(["selected-device-field"]);
    publication.Publish(["selected-device-field"]);
    await selectedWait;
    await selectedNotification.WaitAsync(timeout.Token);
    using var cancellation = new CancellationTokenSource();
    var waiting = selectedNotification.WaitAsync(cancellation.Token).AsTask();
    cancellation.Cancel();
    try { await waiting; throw new InvalidOperationException("No cancellation"); } catch (OperationCanceledException) { }
    Require(backend.Collections == 1, "publication registration and wake do not collect");
}
var singleValue = new CurrentValuePublicationSignal();
using (var first = singleValue.Subscribe())
using (var second = singleValue.Subscribe())
{
    singleValue.Publish();
    await Task.WhenAll(first.WaitAsync(CancellationToken.None).AsTask(), second.WaitAsync(CancellationToken.None).AsTask());
}
var installRoot = Path.Combine(Path.GetTempPath(), "read-write-package-consumer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(installRoot);
var selected = Path.Combine(installRoot, "selected.zip");
var installedDirectory = Path.Combine(installRoot, "module");
try
{
    using (var archive = ZipFile.Open(selected, ZipArchiveMode.Create))
    { using var output = new StreamWriter(archive.CreateEntry("release/recorded.bin").Open()); output.Write("exact selected package"); }
    var result = await new DependencyPackageInstaller().InstallAsync(new("recorded", selected),
        new(installedDirectory, ArchiveFiles: ["recorded.bin"]), default);
    Require(result.Status == "filesExtracted", "selected archive install");
    Require(File.ReadAllText(Path.Combine(installedDirectory, "recorded.bin")) == "exact selected package", "installed module content");
}
finally
{
    File.Delete(selected); File.Delete(Path.Combine(installedDirectory, "recorded.bin"));
    if (Directory.Exists(installedDirectory)) Directory.Delete(installedDirectory);
    Directory.Delete(installRoot);
}
Console.WriteLine("ReadWriteCore package consumer passed: directory, cache, direct collection, exact integer, read-only rejection and selected package install.");

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

sealed class Backend : IReadWriteBackend
{
    private readonly FieldReference reference;
    public ReadWriteCatalog Catalog { get; }
    public int Collections { get; private set; }
    public int Writes { get; private set; }
    public Backend()
    {
        var definition = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.NetworkRxErrors);
        reference = new("network-interface", definition.Name);
        Catalog = new([new("network-interface", "network", null)], [new(reference,
            new(definition.Name, definition.Unit, definition.ValueKind, definition.Meaning),
            new(true, null, []), OperationSupport.Unsupported("read-only"), "explicit-source")]);
    }
    public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken token) => ValueTask.FromResult(Catalog);
    public FieldReading ReadCached(FieldReference field) => new(field, FieldObservationStatus.Current,
        new(Integer: "18446744073709551615"), DateTimeOffset.UnixEpoch, "explicit-source");
    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Collections++;
        return ValueTask.FromResult(fields.Select(ReadCached).ToImmutableArray());
    }
    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken token)
    {
        Writes++; throw new InvalidOperationException("Read-only field reached the backend writer.");
    }
}
