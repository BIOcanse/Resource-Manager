using ResourceManager.App.Domain.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class SecurityProductConsumer
{
    internal static readonly FieldReference Antivirus = new("machine", "security_all_antivirus_products_reported");
    internal static readonly FieldReference Firewall = new("machine", "security_all_firewall_products_reported");

    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedSource();
        using var owner = source.CreateOwner();
        using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "construction and catalog do not query WSC");
        var catalog = await core.DiscoverAsync();
        Require(catalog.Fields.Any(item => item.Field == Antivirus) && catalog.Fields.Any(item => item.Field == Firewall),
            "both original canonical product fields are discovered");
        var capability = core.Query(Antivirus);
        Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported
            && capability.Definition.ValueKind == "structured" && capability.Definition.Unit == "record[]",
            "public structured readonly capability");
        Verify(core.ReadCached(Antivirus));
        var reads = source.Reads;
        Verify(core.ReadCached(Antivirus));
        Require(source.Reads == reads, "cached reads do not capture again");
        var current = (await core.CollectAsync([Antivirus])).Single();
        Verify(current);
        Require(current.Value!.Structure!.Value.GetRawText() == core.ReadCached(Antivirus).Value!.Structure!.Value.GetRawText(),
            "direct collection and original Current agree");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            reads = source.Reads;
            var rejected = false;
            try { _ = await core.CollectAsync([Antivirus], canceled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected && source.Reads == reads, "canceled collection does not query the source");
            Require(core.ReadCached(Antivirus).ObservedAt == current.ObservedAt, "cancellation preserves current display");
            Verify(core.ReadCached(Antivirus));
        }
        source.FailAntivirus = true;
        var partial = await core.CollectAsync([Antivirus, Firewall]);
        Require(partial[0] is { Status: FieldObservationStatus.Unavailable, Value: null }, "failed category clears its value");
        Require(partial[1].Status == FieldObservationStatus.Current
            && partial[1].Value!.Structure!.Value.GetArrayLength() == 0, "independent successful empty category remains available");
        source.FailAntivirus = false;
        Verify((await core.CollectAsync([Antivirus])).Single());
        source.FailWholeCapture = true;
        Require((await core.CollectAsync([Antivirus])).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "whole capture failure clears Current");
        source.FailWholeCapture = false;
        Verify((await core.CollectAsync([Antivirus])).Single());
        Require((await core.WriteAsync(Antivirus, new(Integer: MetricInteger.Parse("1")))).Status == "unsupported", "registration information cannot write policy");
        Console.WriteLine("[SECURITY-PRODUCT-RW-PACKAGE] nine extensions; exact zero/false/uint32/string; legacy nulls; passive Current; category/whole failure; cancellation/recovery; recorded only");
    }

    internal static void Verify(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is not null, "structured current value");
        var products = reading.Value!.Structure!.Value;
        Require(products.GetArrayLength() == 2, "original order and duplicate names are preserved");
        var row = products[0];
        Require(row.GetProperty("nameReported").GetString() == "same\0name"
            && row.GetProperty("stateReported").GetUInt32() == 0
            && row.GetProperty("signatureStatusReported").GetUInt32() == 1
            && row.GetProperty("remediationPathReported").GetString() == ""
            && row.GetProperty("stateTimestampReported").ValueKind == JsonValueKind.Null, "original five fields remain lossless");
        Require(row.GetProperty("productGuidReported").GetString() == "guid\0raw"
            && !row.GetProperty("productIsDefaultReported").GetBoolean(), "BSTR and valid BOOL false");
        var names = new[] { "antivirusScanSubstatusReported", "antivirusSettingsSubstatusReported",
            "antivirusProtectionUpdateSubstatusReported", "firewallDomainProfileSubstatusReported",
            "firewallPrivateProfileSubstatusReported", "firewallPublicProfileSubstatusReported" };
        Require(names.Select(name => row.GetProperty(name).GetUInt32()).SequenceEqual(new uint[] { 2, 3, 0, 1, 2, 3 }),
            "six SDK substatus domains, including valid zero, remain distinct");
        Require(row.GetProperty("antivirusDaysUntilExpiredReported").GetUInt32() == uint.MaxValue, "DWORD does not pass through signed int");
        var legacy = products[1];
        Require(legacy.GetProperty("nameReported").GetString() == "same\0name", "duplicate product is not collapsed");
        foreach (var name in names.Concat(new[] { "productGuidReported", "productIsDefaultReported", "antivirusDaysUntilExpiredReported" }))
            Require(legacy.GetProperty(name).ValueKind == JsonValueKind.Null, "legacy extension is unavailable: " + name);
    }

    internal sealed class RecordedSource : IDeviceTopologyReader
    {
        internal int Reads { get; private set; }
        internal bool FailAntivirus { get; set; }
        internal bool FailWholeCapture { get; set; }

        public DeviceTopologySnapshot ReadSnapshot()
        {
            Reads++;
            if (FailWholeCapture) throw new InvalidOperationException("recorded WSC capture failure");
            var extended = new DeviceTopologySecurityProductInformation("same\0name", 0, 1, "", null)
            {
                ProductGuidReported = "guid\0raw", ProductIsDefaultReported = false,
                AntivirusScanSubstatusReported = 2, AntivirusSettingsSubstatusReported = 3,
                AntivirusProtectionUpdateSubstatusReported = 0, FirewallDomainProfileSubstatusReported = 1,
                FirewallPrivateProfileSubstatusReported = 2, FirewallPublicProfileSubstatusReported = 3,
                AntivirusDaysUntilExpiredReported = uint.MaxValue
            };
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], [])
            {
                SecurityInformation = DeviceTopologySecurityInformation.Unavailable with
                {
                    AntivirusProductsReported = FailAntivirus ? null : [extended, new("same\0name", 0, 1, "", null)],
                    FirewallProductsReported = []
                }
            };
        }

        internal DeviceTopologyCaptureOwner CreateOwner() => new(new NoAcpi(), new(this,
            snapshot => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.SecurityInformation))),
            (snapshot, at) => WindowsInformationFieldProjection.Security(snapshot?.SecurityInformation ?? DeviceTopologySecurityInformation.Unavailable, at),
            static (_, _) => Task.CompletedTask, static _ => new("recorded-security-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));
    }

    private sealed class NoAcpi : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("security products do not query ACPI");
    }

    internal static void Require(bool value, string contract)
    { if (!value) throw new InvalidDataException("Security product installed package: " + contract); }
}
