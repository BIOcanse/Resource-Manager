using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

internal static class AmdHardwareTopologyConsumer
{
    internal static CpuOsTopologyInformation Information(bool overlapping = false)
    {
        int[] ids = [128, 129, 130, 131];
        return new([new("first", 0, true, [128,129]), new("second", 0, true, [130,131])], [])
        {
            Packages = [new("package", ids)],
            ThreadCpuid = ids.Select((id,index) => new CpuThreadCpuidInformation(id,
                Read(0x80000000U+(uint)index, overlapping && index >= 2))).ToArray()
        };
    }

    internal static CpuThreadCpuidData Read(uint apic, bool smallerCcd = false)
    {
        var calls = new HashSet<(int,int)>();
        var result = WindowsCpuThreadCpuidReader.ReadRegisters((leaf, subleaf) =>
        {
            Require(calls.Add((leaf,subleaf)), "one original CPUID read per register");
            return (unchecked((uint)leaf),subleaf) switch
            {
                (0,0) => (1, 0x68747541, 0x444D4163, 0x69746E65),
                (1,0) => (0x00B00F12, 0, 0, 0),
                (0x80000000,0) => (unchecked((int)0x80000026), 0, 0, 0),
                (0x80000026,2) when smallerCcd => (2,4,0x302,unchecked((int)apic)),
                (0x80000026, < 4) => (subleaf+1, unchecked((int)(0xABCD0000U | (2U<<subleaf))),
                    ((subleaf+1)<<8)|subleaf, unchecked((int)apic)),
                (0x80000026,4) => (0,0,4,unchecked((int)apic)),
                _ => (0,0,0,0)
            };
        });
        var topology = result.AmdExtendedTopology ?? throw new InvalidDataException("Missing public Fn26 topology");
        Require(topology.ExtendedApicId == apic, "full-width hardware identity");
        Require(topology.Ccd == (smallerCcd ? new CpuAmdTopologyLevel(2,4) : new CpuAmdTopologyLevel(3,8)),
            "public explicit CCD level");
        return result;
    }

    internal static void AssertMembership(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is not null,
            "canonical current structured membership");
        var members = reading.Value!.Structure!.Value.Deserialize<CpuAmdCcdCoreMembership[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(members.Length == 2 && members.SelectMany(static row => row.LogicalProcessorAddresses).Distinct().Count() == 4,
            "exact two cores and four OS addresses");
        Require(members.All(static row => row.LogicalProcessorAddresses.All(address => address.ProcessorGroup == 2)), "nonzero OS group");
        Require(members.All(static row => row.CoreDeviceIdentity.StartsWith("windows-cpu-core|", StringComparison.Ordinal)
            && row.ComplexDeviceIdentity.StartsWith("amd-cpu-ccx|", StringComparison.Ordinal)), "explicit original core and complex links");
    }

    internal static async Task RunRecordedAsync()
    {
        var captures = 0; var failed = false; var overlapping = false;
        using var owner = new CpuTopologyCaptureOwner(() =>
        {
            captures++;
            if(failed) throw new InvalidOperationException("Recorded CPUID unavailable");
            return new(null, Information(overlapping), DateTimeOffset.UtcNow);
        }, static () => { });
        var backend = owner.CreateBackend(static _ => new(true,null,[]));
        Require(captures == 0 && owner.ReadInformation().Devices.Count == 0, "passive original owner");
        var catalog = await backend.DiscoverAsync(default);
        var membership = catalog.Fields.Single(static row => row.Field.FieldName == "cpu_amd_ccd_core_membership");
        Require(membership.Read.Supported && !membership.Write.Supported, "read-only canonical field");
        var current = owner.ReadState();
        for(var index=0;index<10;index++) AssertMembership(backend.ReadCached(membership.Field));
        Require(captures == 1 && ReferenceEquals(current,owner.ReadState()), "cache does not capture or replace current");
        var information = owner.ReadInformation();
        Require(information.Devices.Count(static row => row.ObjectType == "cpu_ccd") == 1
            && information.Devices.Count(static row => row.ObjectType == "cpu_ccx") == 1, "one exact CCD and complex");
        failed = true;
        Require((await backend.CollectAsync([membership.Field],default)).Single().Value is null
            && backend.ReadCached(membership.Field).Value is null && captures == 2, "failed capture clears known membership");
        failed = false;
        AssertMembership((await backend.CollectAsync([membership.Field],default)).Single());
        Require(captures == 3, "explicit source recovery");
        overlapping = true;
        Require((await backend.CollectAsync([membership.Field],default)).Single().Value is null
            && backend.ReadCached(membership.Field).Value is null && captures == 4, "overlapping decoded CCD domains clear membership");
        overlapping = false;
        AssertMembership((await backend.CollectAsync([membership.Field],default)).Single());
        Require(captures == 5, "recovery after overlapping domains");
        Console.WriteLine("[AMD-HARDWARE-TOPOLOGY-PACKAGE] Fn26 full APIC; explicit CCD/complex/core/group links; original owner/cache/failure/recovery; overlapping domain rejection and recovery; recorded only.");
    }
    private static void Require(bool condition, string reason)
    { if(!condition) throw new InvalidDataException("AMD hardware topology consumer: "+reason); }
}
