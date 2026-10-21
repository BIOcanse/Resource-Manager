using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

internal static class AcpiFirmwareTestData
{
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name)
        => Assert.Single(Assert.Single(snapshot.Devices).Fields, field => field.Definition.Name == name);

    internal static uint Id(string signature) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(signature));

    internal static byte[] Raw(uint id, int length = 40)
    {
        var raw = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, id);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)length);
        raw[8] = 6;
        Encoding.ASCII.GetBytes(" OEM  ").CopyTo(raw, 10);
        Encoding.ASCII.GetBytes("TABLE   ").CopyTo(raw, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24), uint.MaxValue);
        Encoding.ASCII.GetBytes("TEST").CopyTo(raw, 28);
        Checksum(raw);
        return raw;
    }

    internal static IReadOnlyList<AcpiFirmwareTable> Tables(byte revision = 6)
    {
        var id = Id("FACP");
        var raw = Raw(id);
        raw[8] = revision;
        Checksum(raw);
        return [new(id, "FACP", 1, WindowsAcpiFirmwareTableReader.ParseTable(id, raw))];
    }

    internal static void Checksum(byte[] raw)
    {
        raw[9] = 0;
        raw[9] = unchecked((byte)-raw.Sum(static b => (int)b));
    }

}
