using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static IntelMsrCatalogIdentity Core(int index = 0) => new(index, $"windows-cpu-core|{index * 2},{index * 2 + 1}",
        new((ushort)(index * 2 / 64), (byte)(index * 2 % 64)), 0x906E0, null, IntelMsrTargetKind.PhysicalCore, true, true);
    private static IntelMsrCatalogIdentity Package() => new(0, "windows-cpu-package|0:0,0:1", new(0, 0), 0x906E0, null, IntelMsrTargetKind.Package, true, true);
    private static Dictionary<string, IReadOnlySet<MetricFieldCode>> Request(IntelMsrCatalogIdentity target, params MetricFieldCode[] fields)
        => new(StringComparer.Ordinal) { [target.ExactIdentityKey] = fields.ToHashSet() };

    [Theory]
    [InlineData(false, 0, 100)]
    [InlineData(false, 127, 255)]
    [InlineData(false, 127, 1)]
    [InlineData(true, 0, 100)]
    [InlineData(true, 127, 255)]
    public void CorrectTargetAndStatusYieldIndependentCoreAndPackageValues(bool package, int distance, int target)
    {
        var device = package ? Package() : Core();
        var access = new Access();
        using var hardware = new Hardware(access) { TemperatureTarget = (ulong)target << 16 | 15UL << 24,
            CoreStatus = 1UL << 31 | (ulong)distance << 16, PackageStatus = (ulong)distance << 16 };
        var reader = new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access);
        var codes = Codes(package);
        var caller = Environment.CurrentManagedThreadId;
        var result = Assert.Single(reader.Read([device], Request(device, codes)));
        Assert.Equal((double)target, result.Values[codes[0]]);
        Assert.Equal((double)distance, result.Values[codes[1]]);
        Assert.Equal((double)(target - distance), result.Values[codes[2]]);
        Assert.Equal(new uint[] { 0x1A2, package ? 0x1B1U : 0x19CU }, hardware.Reads.Select(static read => read.Register));
        Assert.All(hardware.Reads, read => { Assert.Equal(device.Processor, read.Address); Assert.NotEqual(caller, read.Thread); });
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreValidityIsRequiredButPackageReservedBit31IsIgnored(bool package)
    {
        var device = package ? Package() : Core();
        var access = new Access();
        var hardware = new Hardware(access) { CoreStatus = 20UL << 16, PackageStatus = 20UL << 16 };
        var codes = Codes(package);
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, codes)));
        Assert.Equal(100D, result.Values[codes[0]]);
        Assert.Equal(package, result.Values.ContainsKey(codes[1]));
        Assert.Equal(package, result.Values.ContainsKey(codes[2]));
    }

    [Theory]
    [InlineData(0x1A2)]
    [InlineData(0x19C)]
    public void IndividualReadFailureClearsDependentValuesAndNextCaptureRecovers(int failedRegister)
    {
        var device = Core(); var access = new Access(); var hardware = new Hardware(access) { FailingRegister = (uint)failedRegister };
        var reader = new IntelMsrReader(() => hardware, () => access);
        var codes = Codes(false);
        var failed = Assert.Single(reader.Read([device], Request(device, codes)));
        Assert.False(failed.Values.ContainsKey(codes[2]));
        Assert.Equal(failedRegister != 0x1A2, failed.Values.ContainsKey(codes[0]));
        Assert.Equal(failedRegister != 0x19C, failed.Values.ContainsKey(codes[1]));
        hardware.FailingRegister = null;
        Assert.Equal(3, Assert.Single(reader.Read([device], Request(device, codes))).Values.Count);
        Assert.Equal(2, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void ZeroTargetIsUnavailableWithoutLosingAValidZeroDistance()
    {
        var device = Core(); var access = new Access();
        var hardware = new Hardware(access) { TemperatureTarget = 0, CoreStatus = 1UL << 31 };
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, Codes(false))));
        var value = Assert.Single(result.Values); Assert.Equal(MetricFieldCode.IntelCoreDtsDistance, value.Key); Assert.Equal(0D, value.Value);
    }

    [Theory]
    [InlineData(MetricFieldCode.IntelCoreTemperatureTarget, 0x1A2)]
    [InlineData(MetricFieldCode.IntelCoreDtsDistance, 0x19C)]
    public void FieldSelectionReadsOnlyNecessaryRegisters(MetricFieldCode field, int register)
    {
        var device = Core(); var access = new Access(); var hardware = new Hardware(access);
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device, Core(1)], Request(device, field)));
        Assert.Single(result.Values); Assert.Equal((uint)register, Assert.Single(hardware.Reads).Register);
    }

    [Fact]
    public void UnknownModelCanReadArchitecturalDistanceButCannotReadTemperatureTarget()
    {
        var device = Core() with { Signature = 0xF06E0, SupportsTemperatureTarget = false };
        var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, Codes(false))));
        Assert.Equal(MetricFieldCode.IntelCoreDtsDistance, Assert.Single(result.Values).Key);
        Assert.Equal(0x19CU, Assert.Single(hardware.Reads).Register);
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("signature")]
    [InlineData("apic")]
    [InlineData("support")]
    [InlineData("address")]
    public void ChangedTargetClearsValuesRequestsDirectoryRefreshAndNeverOpensModule(string change)
    {
        var device = Core(); var access = new Access();
        if (change == "vendor") access.Vendor = "AuthenticAMD";
        if (change == "signature") access.Signature++;
        if (change == "apic") device = device with { X2ApicId = 1 };
        if (change == "support") access.ThermalFlags = 0;
        if (change == "address") access.WrongCurrentFromCall = 2;
        var opens = 0;
        var result = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => access)
            .Read([device], Request(device, Codes(false))));
        Assert.Empty(result.Values); Assert.True(result.RequiresTopologyRefresh); Assert.Equal(0, opens);
    }

    [Fact]
    public void AddressChangingDuringMsrReadDiscardsAllValuesAndDisposesBatch()
    {
        var device = Core(); var access = new Access();
        var hardware = new Hardware(access) { AfterRead = () => access.WrongCurrentFromCall = 1 };
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, Codes(false))));
        Assert.Empty(result.Values); Assert.True(result.RequiresTopologyRefresh); Assert.Equal(1, hardware.Disposals);
    }

    [Fact]
    public void EmptyUnsupportedFrozenAndUnselectedDemandDoNotCreateAccessOrOpenHardware()
    {
        var creates = 0;
        var reader = new IntelMsrReader(() => throw new InvalidOperationException(), () => { creates++; throw new InvalidOperationException(); });
        var device = Core();
        Assert.Empty(reader.Read([device], new Dictionary<string, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(reader.Read([device with { SupportsDts = false }], Request(device, Codes(false))));
        Assert.Empty(reader.Read([device with { SupportsTemperatureTarget = false }], Request(device, MetricFieldCode.IntelCoreDtsTemperature)));
        Assert.Empty(reader.Read([device], Request(Core(1), Codes(false))));
        var zone = new IntelMsrMonitoringZone(reader);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(zone.Read([device], Request(device, Codes(false)))); Assert.Equal(0, creates);
        _ = new IntelMsrMonitoringZone(Path.Combine(Path.GetTempPath(), "nonexistent-intel-dependencies"));
    }

    [Fact]
    public void MultipleTargetsShareOneReadOnlyBatchSessionAndFailureToOpenOrCloseClearsValues()
    {
        var one = Core(); var two = Core(1); var access = new Access(); var hardware = new Hardware(access);
        var requested = Request(one, Codes(false)); requested.Add(two.ExactIdentityKey, Codes(false).ToHashSet());
        var reader = new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access);
        Assert.Equal(2, reader.Read([one, two], requested).Count);
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
        Assert.All(hardware.Reads.Take(2), read => Assert.Equal(one.Processor, read.Address));
        Assert.All(hardware.Reads.Skip(2), read => Assert.Equal(two.Processor, read.Address));
        hardware.FailDispose = true;
        Assert.All(reader.Read([one, two], requested), read => Assert.Empty(read.Values));
        var failedOpens = 0;
        Assert.All(new IntelMsrReader(() => { failedOpens++; throw new IOException(); }, () => access).Read([one, two], requested), read => Assert.Empty(read.Values));
        Assert.Equal(1, failedOpens);
    }

    private static MetricFieldCode[] Codes(bool package) => package
        ? [MetricFieldCode.IntelPackageTemperatureTarget, MetricFieldCode.IntelPackageDtsDistance, MetricFieldCode.IntelPackageDtsTemperature]
        : [MetricFieldCode.IntelCoreTemperatureTarget, MetricFieldCode.IntelCoreDtsDistance, MetricFieldCode.IntelCoreDtsTemperature];

    private sealed class Access : ICpuThreadCpuidAccess
    {
        internal CpuProcessorAddress Address = new(0, 0);
        internal uint Signature = 0x906E0;
        internal uint MaximumLeaf = 6;
        internal ushort BaseFrequencyMhz;
        internal string Vendor = "GenuineIntel";
        internal int ThermalFlags = 65;
        internal int FeedbackFlags;
        internal int Leaf1Edx;
        internal int WrongCurrentFromCall = int.MaxValue;
        internal int CurrentCalls;
        internal int SelectionCalls, TotalCurrentCalls, CpuidCaptures;
        public bool Select(CpuProcessorAddress address) { Address = address; CurrentCalls = 0; SelectionCalls++; return true; }
        public CpuProcessorAddress Current()
        {
            TotalCurrentCalls++;
            return ++CurrentCalls >= WrongCurrentFromCall ? new(1, 1) : Address;
        }
        public (int Eax, int Ebx, int Ecx, int Edx) Read(int leaf, int subleaf)
        {
            if (leaf == 0 && subleaf == 0) CpuidCaptures++;
            var vendor = Encoding.ASCII.GetBytes(Vendor);
            return unchecked((uint)leaf) switch
            {
                0 => (unchecked((int)MaximumLeaf), BinaryPrimitives.ReadInt32LittleEndian(vendor.AsSpan(0, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(vendor.AsSpan(8, 4)), BinaryPrimitives.ReadInt32LittleEndian(vendor.AsSpan(4, 4))),
                1 => (unchecked((int)Signature), 0, 0, Leaf1Edx),
                6 => (ThermalFlags, 0, FeedbackFlags, 0),
                7 => (0, 0, 0, 0), // The declared max leaf makes this legal; EAX says there is no subleaf 1.
                0x0B => (0, 0, 0, 0), // The synthetic CPU does not expose extended topology data.
                0x15 => (0, 0, 0, 0),
                0x16 => (BaseFrequencyMhz, 0, 0, 0),
                0x80000000 => (unchecked((int)0x80000000), 0, 0, 0),
                _ => throw new InvalidOperationException($"Unexpected CPUID {leaf:X}/{subleaf:X}")
            };
        }
    }

    private sealed class Hardware(Access access) : IIntelCpuHardware
    {
        internal ulong TemperatureTarget = 100UL << 16;
        internal ulong CoreStatus = 1UL << 31 | 20UL << 16;
        internal ulong PackageStatus = 10UL << 16;
        internal Dictionary<uint, ulong> RaplRegisters = new() { [0x606] = 14UL << 8,
            [0x611] = 4096, [0x639] = 2048, [0x641] = 1024, [0x619] = 512 };
        internal uint? FailingRegister;
        internal bool FailDispose;
        internal Action? AfterRead;
        internal int Opens, Disposals, Writes;
        internal List<(uint Register, CpuProcessorAddress Address, int Thread)> Reads = [];
        public ulong ReadMsr(uint register)
        {
            Reads.Add((register, access.Address, Environment.CurrentManagedThreadId)); AfterRead?.Invoke();
            if (register == FailingRegister) throw new IOException("Fixture read failure");
            return register switch { 0x1A2 => TemperatureTarget, 0x19C => CoreStatus, 0x1B1 => PackageStatus,
                _ => RaplRegisters.TryGetValue(register, out var value) ? value : throw new InvalidOperationException() };
        }
        public void WriteMsr(uint register, ulong value) { Writes++; throw new InvalidOperationException("Monitoring cannot write"); }
        public bool CanWriteMsr(uint register) => throw new InvalidOperationException("Monitoring cannot query write policy");
        public void Dispose() { Disposals++; if (FailDispose) throw new IOException("Fixture close failure"); }
    }
}
