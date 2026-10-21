using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Printers;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsPrinterInformationNativeReaderParserTests
{
    [Fact]
    public void SuccessfulGetPrinterFillIsBoundedByItsReportedBytes()
    {
        const uint sizingBytes = 4096;
        var shortPayloadBytes = checked((uint)Marshal.SizeOf<WindowsPrinterInformationNativeReader.NativePrinterInfo2>() - 1);
        var callCount = 0;
        WindowsPrinterInformationNativeReader.BufferQuery query = (buffer, capacity) =>
        {
            if (callCount++ == 0)
                return new(false, sizingBytes, 0, 122);

            // If the reader accidentally parses the allocation extent instead of the
            // successful byte count, this full, zeroed buffer would look structurally valid.
            Marshal.Copy(new byte[checked((int)capacity)], 0, buffer, checked((int)capacity));
            return new(true, shortPayloadBytes, 1, 0);
        };
        var scope = new WindowsPrinterInformationScope(maximumNativeBufferBytes: (int)sizingBytes);

        Assert.Throws<InvalidDataException>(() => ParseBoundedGetPrinterResponse(query, scope));
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void PrinterInfo4ParserRejectsStringPointerOutsideReportedPayload()
    {
        var structureSize = Marshal.SizeOf<WindowsPrinterInformationNativeReader.NativePrinterInfo4>();
        var buffer = Marshal.AllocHGlobal(checked(structureSize + sizeof(char) * 2));
        try
        {
            var info = new WindowsPrinterInformationNativeReader.NativePrinterInfo4
            {
                PrinterName = IntPtr.Add(buffer, structureSize),
                ServerName = IntPtr.Zero,
                Attributes = 0
            };
            Marshal.StructureToPtr(info, buffer, false);
            Marshal.WriteInt16(info.PrinterName, (short)'Q');
            Marshal.WriteInt16(info.PrinterName, sizeof(char), 0);

            Assert.Throws<InvalidDataException>(() =>
                WindowsPrinterInformationNativeReader.ParsePrinterInfo4Array(buffer, (uint)structureSize, 1,
                    new WindowsPrinterInformationScope(), default));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void PrinterInfo4ParserRejectsTruncatedArrayBeforeMarshalling()
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                WindowsPrinterInformationNativeReader.ParsePrinterInfo4Array(buffer, 1, 1,
                    new WindowsPrinterInformationScope(), default));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ParseBoundedGetPrinterResponse(
        WindowsPrinterInformationNativeReader.BufferQuery query, WindowsPrinterInformationScope scope)
    {
        var totalBufferBytes = 0;
        _ = WindowsPrinterInformationNativeReader.ReadBoundedBuffer(
            query, scope, ref totalBufferBytes, default,
            (buffer, validBytes, count) =>
            {
                if (count != 1) throw new InvalidDataException("GetPrinterW did not return one PRINTER_INFO_2.");
                return WindowsPrinterInformationNativeReader.ParsePrinterInfo2(buffer, validBytes, 32);
            });
    }
}
