using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsUsbSuperSpeedPlusLaneReaderTests
{
    [Fact]
    public void SendsOneBasedIndexAndExactTwentyFourByteSameSizeRequest()
    {
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        var called = false;
        UsbHubIoctlTransport transport = (SafeFileHandle actualHub, uint controlCode, byte[] buffer, out uint bytesReturned) =>
        {
            called = true;
            Assert.Same(hub, actualHub);
            Assert.Equal(WindowsUsbSuperSpeedPlusLaneReader.IoctlCode, controlCode);
            Assert.Equal(24, buffer.Length);
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4)));
            Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4)));

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 0x000A4030);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 0x000A40B0);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), 0);
            bytesReturned = 24;
            return true;
        };

        var result = WindowsUsbSuperSpeedPlusLaneReader.Read(hub, 7, transport);

        Assert.True(called);
        Assert.Equal<UsbSuperSpeedPlusLaneInformation?>(
            new UsbSuperSpeedPlusLaneInformation(7, 24, 0x000A4030, 0, 0x000A40B0, 0), result);
    }

    [Theory]
    [InlineData(false, 24u, 7u, 24u)]
    [InlineData(true, 23u, 7u, 24u)]
    [InlineData(true, 24u, 8u, 24u)]
    [InlineData(true, 24u, 7u, 20u)]
    public void FailureShortReturnWrongPortOrWrongLengthIsUnavailable(
        bool succeeded,
        uint returned,
        uint responsePort,
        uint responseLength)
    {
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        UsbHubIoctlTransport transport = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] buffer, out uint bytesReturned) =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), responsePort);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), responseLength);
            bytesReturned = returned;
            return succeeded;
        };

        Assert.Null(WindowsUsbSuperSpeedPlusLaneReader.Read(hub, 7, transport));
    }

    [Fact]
    public void InvalidHandleOrZeroPortDoesNotInvokeTransport()
    {
        using var invalidHub = new SafeFileHandle(nint.Zero, ownsHandle: false);
        var calls = 0;
        UsbHubIoctlTransport transport = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] ignoredBuffer, out uint bytesReturned) =>
        {
            calls++;
            bytesReturned = 0;
            return false;
        };

        Assert.Null(WindowsUsbSuperSpeedPlusLaneReader.Read(invalidHub, 7, transport));
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        Assert.Null(WindowsUsbSuperSpeedPlusLaneReader.Read(hub, 0, transport));
        Assert.Equal(0, calls);
    }
}
