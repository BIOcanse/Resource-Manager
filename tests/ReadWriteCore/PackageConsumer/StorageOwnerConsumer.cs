using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Platform;

internal static class StorageOwnerConsumer
{
    // Recorded route executes the production matcher and selected capture, not only parsers.
    internal static void Run()
    {
        var queryCalls = 0; var nativeCalls = 0; var fail = false;
        var physical = Row(("DeviceId", 7), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("UniqueIdFormat", 1), ("Size", 100UL), ("BusType", 17));
        var os = Row(("Number", 23), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("Size", 100UL), ("BusType", 17));
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0),
            capabilityQuery: (_, _) => new([], null),
            query: (_, command) => { queryCalls++; return new QueryResult([command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : os], null); },
            readIdentify: (number, protocol, serial, unique, format) =>
            {
                Require(number == 23 && protocol == StorageIdentifyProtocol.NvmeController && serial == "serial" && unique == "unique" && format == 1,
                    "original physical/OS matcher supplies exact opened identity evidence");
                nativeCalls++;
                return fail ? null : new(null, WindowsStorageIdentifyReader.ParseNvmeController(IdentifyPayload()));
            }, readNvme: (number, serial, unique, format) =>
            {
                Require(number == 23 && serial == "serial" && unique == "unique" && format == 1, "selected NVMe read receives same OS target");
                nativeCalls++; return WindowsNvmeHealthReader.ParseHealthLog(new byte[512]);
            });
        var snapshot = owner.ReadSnapshot();
        Require(ReferenceEquals(snapshot, owner.ReadSnapshot()), "complete static capture uses its unique SDK cache");
        var identities = owner.ReadIdentities();
        var identity = identities.Identities.Single().ExactIdentityKey;
        Require(owner.ReadNvmeSensorsCore(new(new Dictionary<int, string>())).Disks.Count == 0, "empty selection does not query");
        var beforeSelected = queryCalls;
        Require(owner.ReadNvmeSensorsCore(new(new Dictionary<int, string> { [7] = identity })).Disks.Count == 1
            && queryCalls == beforeSelected + 2, "selected production query/matcher/read closure");
        var current = WindowsInformationFieldProjection.Storage(owner.ReadInformationDeclarations()!, DateTimeOffset.UtcNow);
        var backend = Backend(() => current);
        var field = new FieldReference(identity, "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes");
        Require(backend.ReadCached(field).Value is not null, "selected Identify capture reaches public canonical backend");
        fail = true;
        current = WindowsInformationFieldProjection.Storage(owner.ReadInformationDeclarations()!, DateTimeOffset.UtcNow);
        Require(backend.ReadCached(field).Value is null && nativeCalls == 3, "failed current payload is empty");
        Require(typeof(WindowsStorageOwner).Assembly == typeof(DeviceTopologyStorageSnapshot).Assembly, "cache DTO lives in independent package");
        Console.WriteLine("Storage owner recorded canonical consumer passed: same capture/cache/matcher, selected native delegates, canonical current and failure clearing.");
    }

    // Actual Windows route is separate and explicit: WMI/DriveInfo and fixed Identify reads only.
    // It does not issue SMART receive commands, poll, install a driver, or require an ATA/NVMe disk.
    internal static void RunWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0));
        var capabilities = owner.ReadSnapshot();
        Require(capabilities.Error is null, "actual whole static capture");
        Require(ReferenceEquals(capabilities, owner.ReadSnapshot()), "actual whole capture cache reused");
        var declarations = owner.ReadInformationDeclarations();
        Require(declarations is not null, "actual physical disk identity enumeration");
        var current = WindowsInformationFieldProjection.Storage(declarations!, DateTimeOffset.UtcNow);
        var backend = Backend(() => current);
        foreach (var device in current.Devices)
            foreach (var field in device.Fields)
            {
                var reference = new FieldReference(device.DeviceIdentity, field.Definition.Name);
                var reading = backend.ReadCached(reference);
                Require((reading.Value is null) == (field.Value is null), "actual canonical capability availability matches captured result");
            }
        Console.WriteLine($"Storage owner consumer: actual static bindings {capabilities.Devices.Count}, Identify devices {current.Devices.Count}; failed native reads remain empty.");
    }

    private static InformationReadWriteBackend Backend(Func<DeviceInformationSnapshot> current)
        => new(current, _ => ValueTask.FromResult(current()), _ => new OperationSupport(true, null, []));
    private static byte[] IdentifyPayload() { var bytes = new byte[4096]; bytes[0] = 1; bytes[80] = 0; bytes[81] = 2; bytes[82] = 1; bytes[256] = 8; bytes[280] = 1; bytes[516] = 1; return bytes; }
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
        => values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
}
