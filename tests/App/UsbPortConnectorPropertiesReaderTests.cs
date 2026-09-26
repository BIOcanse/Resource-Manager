using ResourceManager.App.Infrastructure.DeviceTopology;
using System.Buffers.Binary;
using System.Text;

namespace Resource_Manager_APP.Tests;

public sealed class UsbPortConnectorPropertiesReaderTests
{
    [Fact]
    public void ParseEntry_ReadsFlagsCompanionAndHubName()
    {
        const string hubName = @"\??\USB#ROOT_HUB30";
        var nameBytes = Encoding.Unicode.GetBytes(hubName + '\0');
        var buffer = new byte[16 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, sizeof(uint)), 0b1011);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, sizeof(ushort)), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, sizeof(ushort)), 7);
        nameBytes.CopyTo(buffer, 16);

        var result = WindowsUsbPortConnectorPropertiesReader.ParseEntry(buffer, (uint)buffer.Length);

        Assert.NotNull(result);
        Assert.Equal((uint)0b1011, result.PropertyFlags);
        Assert.Equal((ushort)2, result.CompanionIndex);
        Assert.Equal((ushort)7, result.CompanionPortNumber);
        Assert.Equal(hubName, result.CompanionHubSymbolicLinkName);
    }

    [Fact]
    public void ParseEntry_RejectsShortBuffers()
    {
        Assert.Null(WindowsUsbPortConnectorPropertiesReader.ParseEntry(new byte[15], 15));
        Assert.Null(WindowsUsbPortConnectorPropertiesReader.ParseEntry(new byte[18], 15));
    }

}
