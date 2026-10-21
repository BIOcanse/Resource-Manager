using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

internal static class CpuInstructionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        const int both = (1 << 15) | (1 << 21);
        foreach (var (bit, name, other) in new[] { (15, "USER_MSR", "APX_F"), (21, "APX_F", "USER_MSR") })
        {
            var data = ReadDeclared("GenuineIntel", 1 << bit);
            Require(data.Identity.HardwareFeatures.Contains(name) && !data.Identity.HardwareFeatures.Contains(other),
                "independent Intel hardware declaration");
            Require(data.Identity.OsUsableFeatures.Count == 0, "hardware is not OS enablement");
        }
        var amd = ReadDeclared("AuthenticAMD", both);
        Require(!amd.Identity.HardwareFeatures.Contains("USER_MSR") && !amd.Identity.HardwareFeatures.Contains("APX_F"),
            "Intel meanings do not leak into AMD reserved bits");
        Require(amd.IdentityRegisters.Single(row => row.Leaf == 7 && row.Subleaf == 1).Edx == both,
            "uninterpreted AMD register stays lossless");
        var unsupported = ReadDeclared("GenuineIntel", both, 0);
        Require(!unsupported.Identity.HardwareFeatures.Contains("USER_MSR") && !unsupported.Identity.HardwareFeatures.Contains("APX_F"),
            "subleaf support gate");
        var clear = ReadDeclared("GenuineIntel", 0);
        Require(!clear.Identity.HardwareFeatures.Contains("USER_MSR") && !clear.Identity.HardwareFeatures.Contains("APX_F"),
            "zero bits are not sticky");

        var captures = 0;
        var unavailable = false;
        using var owner = new CpuTopologyCaptureOwner(() =>
        {
            captures++;
            if (unavailable) throw new InvalidOperationException("Recorded CPU source unavailable");
            return new(null, new([new("recorded-core", 0, false, [0])], [])
            {
                ThreadCpuid = [new(0, ReadDeclared("GenuineIntel", both))],
                OsUsableFeatures = []
            }, DateTimeOffset.UtcNow);
        }, static () => { });
        var backend = owner.CreateBackend(static _ => new(true, null, []));
        Require(captures == 0 && owner.ReadInformation().Devices.Count == 0, "passive original owner");
        var catalog = await backend.DiscoverAsync(default);
        var capability = catalog.Fields.Single(row => row.Field.FieldName == "cpu_all_instruction_feature");
        var field = capability.Field;
        var current = owner.ReadState();
        for (var index = 0; index < 10; index++) RequireBoth(backend.ReadCached(field));
        Require(captures == 1 && ReferenceEquals(current, owner.ReadState()), "cached reads do not capture or replace Current");
        Require(capability.Read.Supported && !capability.Write.Supported, "read-only declaration");
        unavailable = true;
        var failed = (await backend.CollectAsync([field], default)).Single();
        Require(failed.Value is null && backend.ReadCached(field).Value is null && captures == 2, "source failure clears current declaration");
        unavailable = false;
        RequireBoth((await backend.CollectAsync([field], default)).Single());
        Require(captures == 3, "original owner recovers on explicit capture");
        Require(typeof(WindowsCpuThreadCpuidReader).Assembly == typeof(ReadWriteCore).Assembly
            && typeof(CpuTopologyCaptureOwner).Assembly == typeof(ReadWriteCore).Assembly, "actual public library owns implementation");
        Console.WriteLine("[CPU-INSTRUCTION-PACKAGE] USER_MSR/APX_F; vendor/subleaf/zero/raw/OS separation; original owner/cache/failure/recovery; recorded only, no CPU/MSR execution.");
    }

    private static void RequireBoth(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is not null,
            "original current structured instruction list");
        var names = reading.Value!.Structure!.Value.EnumerateArray().Select(static row => row.GetString()).ToArray();
        Require(names.Contains("USER_MSR") && names.Contains("APX_F"), "two declarations survive public field projection");
    }

    private static CpuThreadCpuidData ReadDeclared(string vendor, int bits, uint maximumSubleaf = 1)
    {
        var bytes = Encoding.ASCII.GetBytes(vendor);
        var vendorB = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var vendorD = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var vendorC = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var calls = new HashSet<(int, int)>();
        var data = WindowsCpuThreadCpuidReader.ReadRegisters((leaf, subleaf) =>
        {
            Require(calls.Add((leaf, subleaf)), "one actual read per leaf/subleaf");
            return (unchecked((uint)leaf), subleaf) switch
            {
                (0, 0) => (7, vendorB, vendorC, vendorD),
                (1, 0) => (vendor == "AuthenticAMD" ? 0x00A20F12 : 0x000906EA, 0, 0, 1 << 5),
                (6, 0) => (0, 0, 0, 0),
                (7, 0) => (checked((int)maximumSubleaf), 0, 0, 0),
                (7, 1) when maximumSubleaf >= 1 => (0, 0, 0, bits),
                (0x80000000, 0) => (unchecked((int)0x80000000), 0, 0, 0),
                _ => throw new InvalidDataException("Undeclared CPUID leaf requested by public consumer")
            };
        });
        return data;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("CPU instruction consumer: " + message); }
}
