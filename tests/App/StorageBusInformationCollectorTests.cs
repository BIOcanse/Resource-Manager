using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class StorageBusInformationCollectorTests
{
    internal const string Name = "storage_all_bus_type_declared";
    internal const string Source = "windows.storage-device-descriptor";

    [Fact]
    public void OfficialSdkDescriptorsKeepAtaNvmeAndVirtualBusReportsSeparate()
    {
        using var sdk = DiskIdentityInformationCollectorTests.Fixture();
        Assert.Equal(40, sdk.RootElement.GetProperty("descriptorSize").GetInt32()); Assert.Equal(28, sdk.RootElement.GetProperty("busOffset").GetInt32());
        foreach (var (key, bus) in new[] { ("ata", 11U), ("nvme", 17U), ("virtual", 14U) })
        {
            var calls = new Calls { Descriptor = DiskIdentityInformationCollectorTests.Frame(key) };
            var observed = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
            Assert.Equal(bus, observed.BusTypeReported); Assert.Equal(1, calls.DescriptorReads); Assert.Equal(2, calls.NumberReads); Assert.True(calls.Handle!.IsClosed);
            if (key == "virtual") { Assert.Null(observed.HardwareIdentification); Assert.Equal(0, calls.VpdReads); }
        }
    }

    [Theory]
    [InlineData(0U)] [InlineData(7U)] [InlineData(14U)] [InlineData(16U)] [InlineData(20U)] [InlineData(uint.MaxValue)]
    public void RawUnknownUsbVirtualSpacesFabricAndFutureValuesDoNotRequireSerialOrIdentifyProtocol(uint bus)
    {
        var bytes = DiskIdentityInformationCollectorTests.Frame("ata");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 0); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), bus);
        var calls = new Calls { Descriptor = bytes }; var observed = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(bus, observed.BusTypeReported); Assert.Null(observed.HardwareIdentification); Assert.NotNull(observed.Layout);
        Assert.Equal(1, calls.DescriptorReads); Assert.Equal(0, calls.VpdReads); Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void MissingDirtySerialAndFailedVpdCannotClearValidBusType(int failure)
    {
        var calls = new Calls();
        if (failure == 0) BinaryPrimitives.WriteUInt32LittleEndian(calls.Descriptor.AsSpan(24), 0);
        if (failure == 1) BinaryPrimitives.WriteUInt32LittleEndian(calls.Descriptor.AsSpan(24), (uint)calls.Descriptor.Length);
        if (failure == 2) calls.Descriptor.AsSpan(40).Fill(65);
        if (failure is 3 or 4) calls.VpdFailure = failure;
        var observed = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(11U, observed.BusTypeReported); Assert.NotNull(observed.Layout); Assert.Equal(1, calls.DescriptorReads);
        if (failure < 3) { Assert.Null(observed.HardwareIdentification); Assert.Equal(0, calls.VpdReads); }
        else { Assert.NotNull(observed.HardwareIdentification); Assert.Null(observed.HardwareIdentification.DeviceIdentifiers); Assert.Equal(1, calls.VpdReads); }
        Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void FailedOrIncompleteDescriptorDoesNotUseDirtyOrOutOfBoundsBusAndKeepsLayout(int failure)
    {
        var calls = new Calls { DescriptorFailure = failure }; var observed = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Null(observed.BusTypeReported); Assert.Null(observed.HardwareIdentification); Assert.NotNull(observed.Layout);
        Assert.Equal(1, calls.DescriptorReads); Assert.Equal(0, calls.VpdReads); Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NumberAndHandleFailureInvalidateBusButLayoutFailureDoesNot(int failure)
    {
        var calls = new Calls { InvalidOpen = failure == 0, FailedBeforeNumber = failure == 1, ChangedAfterNumber = failure == 2, FailLayout = failure == 3 };
        var observed = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA);
        if (failure == 3) { Assert.Equal(11U, observed!.BusTypeReported); Assert.Null(observed.Layout); Assert.Equal(1, calls.DescriptorReads); }
        else Assert.Null(observed);
        Assert.True(calls.Handle!.IsClosed); if (failure < 2) Assert.Equal(0, calls.DescriptorReads);
    }

    [Fact]
    public void OriginalReaderProjectsExactEnumAndSemanticHashIncludesChangesButNoHistoricalCurrentValue()
    {
        var api = new Api(); var reader = new WindowsDiskLayoutInformationReader(api);
        var disk = Assert.Single(reader.Read()!); Assert.Equal(14U, disk.BusTypeReported);
        var raw = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [disk] };
        var comparer = new DeviceTopologySemanticComparer(); var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(restored));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { DiskLayoutInformation = [disk with { BusTypeReported = 0 }] }));
        var field = DiskLayoutInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(restored, DateTimeOffset.UtcNow), disk.DeviceIdentity, Name);
        Assert.Equal("14", field.Value!.Value.GetString()); Assert.Equal(Source, field.Definition.SourceId); Assert.Equal("storage", field.Definition.Resource); Assert.Equal("all", field.Definition.Vendor);
        Assert.Null(DiskLayoutInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(restored, null), disk.DeviceIdentity, Name).Value);
    }

    internal sealed class Api : IWindowsDiskLayoutInformationApi
    {
        internal int Reads; internal uint? Bus = 14;
        internal IReadOnlyList<WindowsDiskInterface>? Directory = [DiskLayoutInformationCollectorTests.DiskA];
        public IReadOnlyList<WindowsDiskInterface>? Enumerate() => Directory;
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device)
        {
            Interlocked.Increment(ref Reads);
            return new(2, WindowsDiskLayoutInformationDecoder.Layout(DiskLayoutInformationCollectorTests.Frame("raw"))) { BusTypeReported = Bus };
        }
    }

    private sealed class Calls : IWindowsDiskLayoutNativeCalls
    {
        internal byte[] Descriptor = DiskIdentityInformationCollectorTests.Frame("ata");
        internal SafeFileHandle? Handle; internal int DescriptorReads, VpdReads, NumberReads;
        internal int DescriptorFailure = -1, VpdFailure = -1; internal bool InvalidOpen, FailedBeforeNumber, ChangedAfterNumber, FailLayout;
        public SafeFileHandle Open(string path) { Assert.Equal(DiskLayoutInformationCollectorTests.DiskA.DevicePath, path); return Handle = new(new IntPtr(InvalidOpen ? -1 : 42), false); }
        private void Check(SafeFileHandle handle) { Assert.Same(Handle, handle); Assert.False(handle.IsClosed); }
        public bool Number(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle); NumberReads++; DiskLayoutInformationCollectorTests.NumberFrame(ChangedAfterNumber && NumberReads == 2 ? 3U : 2U).CopyTo(buffer, 0);
            returned = 12; return !(FailedBeforeNumber && NumberReads == 1);
        }
        public bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle); if (FailLayout) throw new IOException("layout failed");
            var frame = DiskLayoutInformationCollectorTests.Frame("raw"); frame.CopyTo(buffer, 0); returned = (uint)frame.Length; return true;
        }
        public bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned) { Check(handle); returned = 0; return false; }
        public int LastError() => 50;
        public bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        {
            Check(handle); Assert.Equal(12, query.Length); Assert.Equal(new byte[8], query[4..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(query) == 2)
            {
                VpdReads++; if (VpdFailure == 3) throw new IOException("VPD failed");
                returned = 0; return VpdFailure != 4;
            }
            Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(query)); DescriptorReads++; Assert.Equal(4096, buffer.Length);
            Descriptor.CopyTo(buffer, 0); returned = (uint)Descriptor.Length;
            switch (DescriptorFailure)
            {
                case 0: return false;
                case 1: returned = (uint)buffer.Length + 1; break;
                case 2: returned = 7; break;
                case 3: BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), returned + 1); break;
                case 4: BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 35); break;
                case 5: BinaryPrimitives.WriteUInt32LittleEndian(buffer, 35); break;
                case 6: BinaryPrimitives.WriteUInt32LittleEndian(buffer, returned + 1); break;
                case 7: returned--; break;
                case 8: throw new IOException("descriptor failed");
            }
            return true;
        }
    }
}
