using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class OpenClCapabilityConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var target = new WindowsOpenClAdapterTarget(0x0000_0001_0000_002A, 1);
        var scope = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.All, [target]);
        var reader = new RecordedProvider();
        var access = owner.CreateOpenClCapabilityInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_opencl_device_version").Field;
        var extension = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_opencl_extension"
            && field.Field.DeviceIdentity.EndsWith(
                "token-utf8-hex:636C5F6B68725F6465766963655F75756964", StringComparison.Ordinal)).Field;
        Require(version.DeviceIdentity.Contains("luid:000000010000002A|node:00000001", StringComparison.Ordinal),
            "public identity keeps the exact DXGI LUID and OpenCL node mask");
        Require(version.DeviceIdentity.Contains("device:00112233445566778899AABBCCDDEEFF", StringComparison.Ordinal),
            "public identity keeps the exact device UUID");
        Require(backend.ReadCached(version).Value?.Text == "OpenCL 3.0 package-recorded",
            "recorded raw OpenCL device-version string");
        Require(backend.ReadCached(extension).Value?.Text == "cl_khr_device_uuid",
            "recorded exact OpenCL extension token");
        Require(reader.Requests.Single() == WindowsOpenClCapabilityFields.All,
            "explicit full field scope used for discovery");

        var readsBeforeCache = reader.Requests.Count;
        _ = backend.ReadCached(version);
        _ = backend.ReadCached(extension);
        Require(reader.Requests.Count == readsBeforeCache, "cached reads do not sample the provider");

        var selected = (await backend.CollectAsync([version], CancellationToken.None)).Single();
        Require(selected.Status == FieldObservationStatus.Current
            && selected.Value?.Text == "OpenCL 3.0 package-recorded", "selected public device-version collection");
        Require(reader.Requests.Last() == WindowsOpenClCapabilityFields.DeviceVersion,
            "selected collection queries only the requested canonical field");

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = borrow.Bind([extension]).Single();
        Require(binding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId]),
            "role-2 borrow binds the original topology item");
        var borrowed = await borrow.CollectAsync([extension], new Permit(), CancellationToken.None);
        Require(borrowed.Committed && borrowed.Readings.Single().Status == FieldObservationStatus.Current,
            "role-2 extension read commits through the original owner");
        Require(reader.Requests.Last() == WindowsOpenClCapabilityFields.Extensions,
            "role-2 read queries only the selected extension field");
        Require(backend.ReadCached(extension) == borrowed.Readings.Single(),
            "subscription and direct reads share one current value");
        Require(owner.ReadState().InformationFields.FindField(extension) is not null,
            "OpenCL capability resides in the original topology current");
        Console.WriteLine("[OPENCL-CAPABILITY-PACKAGE] recorded LUID/node/UUID identity, raw version, exact extension, selected reads, cache, and role-2 borrow passed; no OpenCL native read.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"OpenCL package consumer failed: {label}.");
    }

    private sealed class RecordedProvider : IWindowsOpenClCapabilityReader
    {
        private readonly List<WindowsOpenClCapabilityFields> requests = [];
        private readonly object gate = new();
        public IReadOnlyList<WindowsOpenClCapabilityFields> Requests { get { lock (gate) return requests.ToArray(); } }

        public WindowsOpenClCapabilityReadResult Read(WindowsOpenClCapabilityScope scope,
            WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) requests.Add(selectedFields);
            var identity = new WindowsOpenClDeviceIdentity(scope.Targets.Single(),
                "00112233445566778899AABBCCDDEEFF", "FFEEDDCCBBAA99887766554433221100");
            return new(true, [new(identity,
                (selectedFields & WindowsOpenClCapabilityFields.DeviceVersion) != 0 ? "OpenCL 3.0 package-recorded" : null,
                (selectedFields & WindowsOpenClCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("cl_khr_device_uuid", "cl_khr_fp64") : null)]);
        }
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
