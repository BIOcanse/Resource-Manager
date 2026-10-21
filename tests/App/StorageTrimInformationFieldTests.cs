using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace Resource_Manager_APP.Tests;

public sealed class StorageTrimInformationFieldTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameOpenedIdentityQueriesPropertyOnlyAndFalseIsAvailable(bool enabled)
    {
        var device = new Device { Response = Trim(enabled) }; var opens = 0;
        var result = WindowsStorageIdentifyReader.ReadTrim(23, "serial", "01020304", 2, 7,
            number => { Assert.Equal(23, number); opens++; return device; });
        Assert.Equal(enabled, result); Assert.Equal(1, opens); Assert.Equal(new uint[] { 0, 2, 8 }, device.Properties); Assert.True(device.Disposed);
        Assert.Equal(new byte[] { 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, WindowsStoragePropertyQuery.Create(8));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ChangedIdentityUnsupportedPropertyPermissionOrInvalidOpenRemainNull(int failure)
    {
        var device = new Device();
        if (failure == 0) device.Serial = "different";
        if (failure == 1) device.Identifiers = Identifier([9, 8]);
        if (failure == 2) device.Response = null;
        if (failure == 3) device.DenyTrim = true;
        var result = WindowsStorageIdentifyReader.ReadTrim(23, "serial", "01020304", 2, 7, _ => failure == 4 ? null : device);
        Assert.Null(result); if (failure != 4) Assert.True(device.Disposed);
        if (failure < 2) Assert.DoesNotContain(8U, device.Properties);
        device = new(); Assert.Null(WindowsStorageIdentifyReader.ReadTrim(23, "serial", null, null, 17, _ => device));
        Assert.DoesNotContain(8U, device.Properties); Assert.True(device.Disposed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void TruncatedMalformedOrOversizedTrimReportsDoNotBecomeFalse(int failure)
    {
        var bytes = Trim(false);
        switch (failure)
        {
            case 0: bytes = bytes[..8]; break;
            case 1: Put(bytes, 0, 8); break;
            case 2: Put(bytes, 4, 11); break;
            case 3: Put(bytes, 4, 13); break;
            case 4: bytes[8] = 2; break;
            case 5: bytes = new byte[4097]; Put(bytes, 0, 12); Put(bytes, 4, 4097); break;
        }
        Assert.Null(WindowsStorageTrimQuery.Decode(bytes));
    }

    [Fact]
    public async Task SelectedOwnerMatcherAndCanonicalProjectionKeepExactIdentityAndClearFailures()
    {
        var physical = Row(("DeviceId", 7), ("UniqueId", "01020304"), ("UniqueIdFormat", 2), ("SerialNumber", "serial"), ("BusType", 7), ("Size", 100UL));
        var os = new Dictionary<string, object?>(physical) { ["Number"] = 23 };
        var calls = 0; var reads = 0; var failed = false; var duplicate = false;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0),
            query: (_, command) => { calls++; return new(command.Contains("MSFT_PhysicalDisk") ? [physical] : duplicate ? [os, os] : [os], null); },
            readTrim: (number, serial, unique, format, bus) =>
            {
                reads++; Assert.Equal(23, number); Assert.Equal("serial", serial); Assert.Equal("01020304", unique); Assert.Equal(2, format); Assert.Equal(7U, bus);
                return WindowsStorageIdentifyReader.ReadTrim(number, serial, unique, format, bus, _ => new Device { Response = failed ? null : Trim(false) });
            });
        var identity = WindowsStorageDeviceIdentity.Create("01020304", "serial");
        var request = new StorageSensorReadRequest(new Dictionary<int, string> { [7] = identity });
        var current = owner.ReadTrimFields(request);
        var backend = new InformationReadWriteBackend(() => current, ct => { ct.ThrowIfCancellationRequested(); current = owner.ReadTrimFields(request); return ValueTask.FromResult(current); }, _ => new(true, null, []));
        var field = new FieldReference(identity, "storage_all_trim_capability");
        Assert.False(backend.ReadCached(field).Value!.Boolean); Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(field).Status);
        Assert.False(backend.Catalog.Require(field).Write.Supported); Assert.Empty(backend.Catalog.Require(field).Read.Dependencies);
        failed = true; var unavailable = await backend.CollectAsync([field], default);
        Assert.Null(unavailable.Single().Value); Assert.Equal(field, unavailable.Single().Field);
        duplicate = true; var before = reads; Assert.Null(InformationFieldProjection.Read(identity, owner.ReadTrimFields(request).FindField(field)!, DateTimeOffset.UtcNow).Value);
        Assert.Equal(before, reads);
        var queriesBefore = calls; Assert.Empty(owner.ReadTrimFields(new(new Dictionary<int, string>())).Devices); Assert.Equal(queriesBefore, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HostDiskRouteUsesTheSameQueryOnOriginalHandleAndOriginalProjection(bool enabled)
    {
        var calls = new DiskCalls { Enabled = enabled }; var native = new WindowsDiskLayoutInformationNativeApi(calls);
        var captured = native.Read(DiskLayoutInformationCollectorTests.DiskA, includeTrim: true)!;
        Assert.Equal(enabled, captured.TrimEnabledReported); Assert.Equal(new uint[] { 0, 8 }, calls.Properties);
        Assert.Equal(2, calls.NumberReads); Assert.True(calls.Handle!.IsClosed);
        var dto = new DeviceTopologyDiskLayoutInformation("windows-pnp|RECORDED") { OsDiskNumberReported = captured.DiskNumber, TrimEnabledReported = captured.TrimEnabledReported };
        var raw = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [dto] };
        var product = DeviceTopologyInformationProjection.Create(raw, raw.CapturedAt);
        var independent = WindowsInformationFieldProjection.Disks([dto], null, null, raw.CapturedAt);
        var field = new FieldReference(dto.DeviceIdentity, "storage_all_trim_capability");
        Assert.Equal(enabled, product.FindField(field)!.Value!.Value.GetBoolean());
        Assert.Equal(product.FindField(field)!.Value!.Value.GetRawText(), independent.FindField(field)!.Value!.Value.GetRawText());
        calls = new() { FailTrim = true }; Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA, true)!.TrimEnabledReported);
        Assert.True(calls.Handle!.IsClosed);
        calls = new() { ChangedNumber = true }; Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA, true));
        Assert.True(calls.Handle!.IsClosed);
        calls = new(); native = new(calls); native.Read(DiskLayoutInformationCollectorTests.DiskA); Assert.DoesNotContain(8U, calls.Properties); // Prior direct method retains its query sequence.
    }

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) => values.ToDictionary(item => item.Name, item => item.Value);
    private static byte[] Trim(bool enabled) { var bytes = new byte[12]; Put(bytes, 0, 12); Put(bytes, 4, 12); bytes[8] = enabled ? (byte)1 : (byte)0; return bytes; }
    private static byte[] Descriptor(string serial) { var bytes = new byte[37 + serial.Length]; Put(bytes, 0, 36); Put(bytes, 4, (uint)bytes.Length); Put(bytes, 24, 36); Put(bytes, 28, 7); Encoding.ASCII.GetBytes(serial).CopyTo(bytes, 36); return bytes; }
    private static byte[] Identifier(byte[] value) { var bytes = new byte[28 + value.Length]; Put(bytes, 0, 16); Put(bytes, 4, (uint)bytes.Length); Put(bytes, 8, 1); Put(bytes, 12, 1); Put(bytes, 16, 2); Put(bytes, 24, 0); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)value.Length); value.CopyTo(bytes, 28); return bytes; }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private sealed class Device : IWindowsStorageIdentifyQuery
    {
        internal string Serial = "serial"; internal byte[]? Identifiers = Identifier([1, 2, 3, 4]); internal byte[]? Response = Trim(true);
        internal bool Disposed, DenyTrim; internal readonly List<uint> Properties = [];
        public byte[]? Query(byte[] input, int capacity)
        {
            var property = BinaryPrimitives.ReadUInt32LittleEndian(input); Properties.Add(property); Assert.Equal(4096, capacity);
            Assert.Equal(WindowsStoragePropertyQuery.Create(property), input);
            return property switch { 0 => Descriptor(Serial), 2 => Identifiers, 8 => DenyTrim ? throw new UnauthorizedAccessException() : Response, _ => throw new InvalidOperationException("Unexpected command") };
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class DiskCalls : IWindowsDiskLayoutNativeCalls
    {
        internal SafeFileHandle? Handle; internal readonly List<uint> Properties = []; internal bool Enabled, FailTrim, ChangedNumber; internal int NumberReads;
        public SafeFileHandle Open(string path) => Handle = new(new IntPtr(42), false);
        public bool Number(SafeFileHandle handle, byte[] output, out uint returned)
        { Assert.Same(Handle, handle); NumberReads++; DiskLayoutInformationCollectorTests.NumberFrame(ChangedNumber && NumberReads == 2 ? 3U : 2U).CopyTo(output, 0); returned = 12; return true; }
        public bool Layout(SafeFileHandle handle, byte[] output, out uint returned) { var frame = DiskLayoutInformationCollectorTests.Frame("raw"); frame.CopyTo(output, 0); returned = (uint)frame.Length; return true; }
        public bool AccessAlignment(SafeFileHandle handle, byte[] input, byte[] output, out uint returned) { returned = 0; return false; }
        public bool DeviceIdentity(SafeFileHandle handle, byte[] input, byte[] output, out uint returned)
        {
            Assert.Same(Handle, handle); Assert.False(handle.IsClosed); var property = BinaryPrimitives.ReadUInt32LittleEndian(input); Properties.Add(property);
            Assert.Equal(WindowsStoragePropertyQuery.Create(property), input);
            if (property == 0) { var descriptor = Descriptor("serial"); descriptor.CopyTo(output, 0); returned = (uint)descriptor.Length; return true; }
            Assert.Equal(8U, property); var bytes = Trim(Enabled); bytes.CopyTo(output, 0); returned = 12; return !FailTrim;
        }
        public int LastError() => 50;
    }
}
