using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsUsbDescriptorReaderBosTests
{
    [Fact]
    public void ReadsBoundedBosHeaderThenCompleteBosOnTheSameHubAndPort()
    {
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        var bos = BosWithOrdinarySuperSpeedCapability();
        var requests = 0;
        UsbHubIoctlTransport transport = (SafeFileHandle actualHub, uint controlCode, byte[] buffer, out uint bytesReturned) =>
        {
            requests++;
            Assert.Same(hub, actualHub);
            Assert.Equal(0x00220410u, controlCode);
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4)));
            Assert.Equal((ushort)0x0F00, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2)));
            Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8, 2)));
            var requestedLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10, 2));
            Assert.InRange(requestedLength, (ushort)5, ushort.MaxValue);
            Assert.Equal(12 + requestedLength, buffer.Length);

            var response = requestedLength == 5 ? bos[..5] : bos;
            Assert.Equal((int)requestedLength, response.Length);
            response.AsSpan().CopyTo(buffer.AsSpan(12));
            bytesReturned = (uint)(12 + response.Length);
            return true;
        };

        var result = WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(hub, 7, transport);

        Assert.Equal<double?>(5000d, result);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void ReadsSspDeclarationFromOneBoundedBosFlowAndKeepsScalarUnavailable()
    {
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        var bos = BosWithSuperSpeedPlusCapability();
        var requestedLengths = new List<ushort>();
        UsbHubIoctlTransport transport = (SafeFileHandle actualHub, uint controlCode, byte[] buffer, out uint bytesReturned) =>
        {
            Assert.Same(hub, actualHub);
            Assert.Equal(0x00220410u, controlCode);
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4)));
            Assert.Equal((ushort)0x0F00, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2)));
            var requestedLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10, 2));
            requestedLengths.Add(requestedLength);
            var response = requestedLength == 5 ? bos[..5] : bos;
            response.AsSpan().CopyTo(buffer.AsSpan(12));
            bytesReturned = (uint)(12 + response.Length);
            return true;
        };

        var result = WindowsUsbDescriptorReader.ReadBosCapabilities(hub, 7, transport);
        var declaration = result.SuperSpeedPlusCapability
            ?? throw new InvalidOperationException("Expected a typed SSP declaration from the bounded BOS response.");

        Assert.Null(result.DeclaredMaximumLinkSpeedMegabitsPerSecond);
        Assert.Equal(0x00000001u, declaration.RawBmAttributes);
        Assert.Equal((ushort)0x1100, declaration.RawFunctionalitySupport);
        Assert.Equal(new ushort[] { 5, (ushort)bos.Length }, requestedLengths);
        Assert.Equal(2, declaration.SublinkSpeedAttributes.Length);
        Assert.Equal(0x000A4030u, declaration.SublinkSpeedAttributes[0].RawValue);
        Assert.Equal(0x000A40B0u, declaration.SublinkSpeedAttributes[1].RawValue);
    }

    [Fact]
    public void InvalidBosLengthOrShortDescriptorResponseStaysUnavailable()
    {
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        var calls = 0;
        UsbHubIoctlTransport invalidLength = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] buffer, out uint bytesReturned) =>
        {
            calls++;
            var header = new byte[] { 5, 0x0F, 4, 0, 1 };
            header.AsSpan().CopyTo(buffer.AsSpan(12));
            bytesReturned = 17;
            return true;
        };
        Assert.Null(WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(hub, 7, invalidLength));
        Assert.Equal(1, calls);

        UsbHubIoctlTransport shortHeader = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] buffer, out uint bytesReturned) =>
        {
            calls++;
            var header = new byte[] { 5, 0x0F, 15, 0, 1 };
            header.AsSpan(0, 4).CopyTo(buffer.AsSpan(12));
            bytesReturned = 16;
            return true;
        };
        Assert.Null(WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(hub, 7, shortHeader));
        Assert.Equal(2, calls);

        var bos = BosWithOrdinarySuperSpeedCapability();
        UsbHubIoctlTransport shortBos = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] buffer, out uint bytesReturned) =>
        {
            calls++;
            var requestedLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10, 2));
            var response = requestedLength == 5 ? bos[..5] : bos[..^1];
            response.AsSpan().CopyTo(buffer.AsSpan(12));
            bytesReturned = (uint)(12 + response.Length);
            return true;
        };
        Assert.Null(WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(hub, 7, shortBos));
        Assert.Equal(4, calls);
    }

    [Fact]
    public void InvalidHandleOrZeroPortDoesNotIssueDescriptorIoctl()
    {
        using var invalidHub = new SafeFileHandle(nint.Zero, ownsHandle: false);
        using var hub = new SafeFileHandle(new nint(123), ownsHandle: false);
        var calls = 0;
        UsbHubIoctlTransport transport = (SafeFileHandle ignoredHub, uint ignoredCode, byte[] ignoredBuffer, out uint bytesReturned) =>
        {
            calls++;
            bytesReturned = 0;
            return false;
        };

        Assert.Null(WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(invalidHub, 7, transport));
        Assert.Null(WindowsUsbDescriptorReader.ReadDeclaredMaximumLinkSpeed(hub, 0, transport));
        Assert.Equal(0, calls);
    }

    private static byte[] BosWithOrdinarySuperSpeedCapability()
        => [5, 0x0F, 15, 0, 1, 10, 0x10, 0x03, 0, 0x08, 0, 3, 0, 0, 0];

    private static byte[] BosWithSuperSpeedPlusCapability()
    {
        var superspeed = new byte[] { 10, 0x10, 0x03, 0, 0x08, 0, 3, 0, 0, 0 };
        var superSpeedPlus = new byte[]
        {
            20, 0x10, 0x0A, 0, 1, 0, 0, 0, 0, 0x11, 0, 0,
            0x30, 0x40, 0x0A, 0, 0xB0, 0x40, 0x0A, 0
        };
        var bos = new byte[5 + superspeed.Length + superSpeedPlus.Length];
        bos[0] = 5;
        bos[1] = 0x0F;
        BinaryPrimitives.WriteUInt16LittleEndian(bos.AsSpan(2, sizeof(ushort)), (ushort)bos.Length);
        bos[4] = 2;
        superspeed.CopyTo(bos, 5);
        superSpeedPlus.CopyTo(bos, 5 + superspeed.Length);
        return bos;
    }
}
