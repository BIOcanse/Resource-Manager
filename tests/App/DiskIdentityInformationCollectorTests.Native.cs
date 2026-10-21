using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskIdentityInformationCollectorTests
{
    [Fact]
    public void BothFixedIdentityGetsShareLayoutHandleAndNumberValidationAndClose()
    {
        var calls = new IdentityCalls(); var read = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(new[] { "number", "layout", "alignment", "identity0", "identity2", "number" }, calls.Order);
        Assert.Equal(StorageIdentifyProtocol.Ata, read.HardwareIdentification!.Protocol); Assert.Equal("SERIAL-A", read.HardwareIdentification.SerialNumber);
        Assert.Equal(Frame("vpd"), read.HardwareIdentification.DeviceIdentifiers); Assert.True(calls.Handle!.IsClosed);
        Assert.NotNull(read.Layout); Assert.NotNull(read.AccessAlignment);
        var collector = new WindowsDiskLayoutInformationReader(new IdentityApi(calls)); var disk = Assert.Single(collector.Read()!);
        Assert.Equal(Frame("vpd"), disk.HardwareIdentification!.DeviceIdentifiers); Assert.Equal(HardwareId, Relation(disk, [Hardware()]));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void FailedDirtyVirtualOrTruncatedDescriptorsDoNotEraseLayoutOrGeometry(int failure)
    {
        var calls = new IdentityCalls { DescriptorFailure = failure };
        var read = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Null(read.HardwareIdentification); Assert.NotNull(read.Layout); Assert.NotNull(read.AccessAlignment); Assert.Equal(2U, read.DiskNumber);
        Assert.DoesNotContain("identity2", calls.Order); Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingVpdRetainsOnlyProtocolAndSerialAndCannotDowngradeAUniqueId(bool throws)
    {
        var calls = new IdentityCalls { VpdFailure = true, VpdThrows = throws };
        var read = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal("SERIAL-A", read.HardwareIdentification!.SerialNumber); Assert.Null(read.HardwareIdentification.DeviceIdentifiers);
        Assert.Null(Relation(Disk() with { HardwareIdentification = read.HardwareIdentification }, [Hardware()]));
        Assert.NotNull(read.Layout); Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public void NvmeAndInvalidOrChangedNumbersRespectTypedIdentityScope()
    {
        var calls = new IdentityCalls { Nvme = true };
        var read = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(StorageIdentifyProtocol.NvmeController, read.HardwareIdentification!.Protocol); Assert.Equal("SERIAL-N", read.HardwareIdentification.SerialNumber);
        calls = new IdentityCalls { ChangedNumber = true }; Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)); Assert.True(calls.Handle!.IsClosed);
        calls = new IdentityCalls { InvalidNumber = true }; Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA));
        Assert.Equal(new[] { "number" }, calls.Order); Assert.True(calls.Handle!.IsClosed);
    }

    private sealed class IdentityApi(IdentityCalls calls) : IWindowsDiskLayoutInformationApi
    {
        public IReadOnlyList<WindowsDiskInterface>? Enumerate() => [DiskLayoutInformationCollectorTests.DiskA];
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device) => new WindowsDiskLayoutInformationNativeApi(calls).Read(device);
    }

    private sealed class IdentityCalls : IWindowsDiskLayoutNativeCalls
    {
        internal SafeFileHandle? Handle; internal List<string> Order = [];
        internal int DescriptorFailure = -1, NumberReads;
        internal bool VpdFailure, VpdThrows, ChangedNumber, InvalidNumber, Nvme;
        public SafeFileHandle Open(string path) { Assert.Equal(DiskLayoutInformationCollectorTests.DiskA.DevicePath, path); NumberReads = 0; return Handle = new(new IntPtr(42), false); }
        public bool Number(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle); Order.Add("number"); NumberReads++;
            DiskLayoutInformationCollectorTests.NumberFrame(ChangedNumber && NumberReads == 2 ? 3U : 2U).CopyTo(buffer, 0); returned = 12; return !InvalidNumber;
        }
        public bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned)
        { Check(handle); Order.Add("layout"); var bytes = DiskLayoutInformationCollectorTests.Frame("raw"); bytes.CopyTo(buffer, 0); returned = (uint)bytes.Length; return true; }
        public bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        { Check(handle); Order.Add("alignment"); DiskAlignmentInformationCollectorTests.Frame().CopyTo(buffer, 0); returned = 28; return true; }
        public bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        {
            Check(handle); Assert.Equal(12, query.Length); Assert.All(query.Skip(4), item => Assert.Equal((byte)0, item));
            Assert.Equal(WindowsDiskLayoutInformationNativeApi.IdentityPropertyCapacity, buffer.Length);
            var property = BinaryPrimitives.ReadUInt32LittleEndian(query); Assert.True(property is 0 or 2); Order.Add("identity" + property);
            var bytes = Frame(property == 2 ? "vpd" : DescriptorFailure == 3 ? "virtual" : Nvme ? "nvme" : "ata");
            bytes.CopyTo(buffer, 0); returned = (uint)bytes.Length;
            if (property == 2) { if (VpdThrows) throw new IOException("VPD failed"); return !VpdFailure; }
            switch (DescriptorFailure)
            {
                case 0: return false;
                case 1: returned = (uint)buffer.Length + 1; break;
                case 2: returned--; break;
                case 4: BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0); break;
                case 5: throw new IOException("descriptor failed");
                case 6: BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 7); break;
            }
            return true;
        }
        public int LastError() => 5;
        private void Check(SafeFileHandle handle) { Assert.Same(Handle, handle); Assert.False(handle.IsClosed); }
    }
}
