using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed class StorageIdentifierTerminalOffsetTests
{
    private const string UniqueId = "eui.1122334455667788";

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public void CountedLastIdentifierMayTerminateAtZeroOrExactAlignedEnd(ushort next)
    {
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(Identifier(next), UniqueId, 8));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Identifier(next), "eui.8877665544332211", 8));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Identifier(next), UniqueId, 3));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(35)]
    [InlineData(39)]
    [InlineData(41)]
    public void NonzeroLastOffsetCannotPointInsideOrBeyondTheCountedDescriptor(ushort next)
        => Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Identifier(next), UniqueId, 8));

    [Fact]
    public void EndTerminatorDoesNotPermitNonzeroPaddingOrAnUndeclaredAdditionalIdentifier()
    {
        var padding = Identifier(40);
        padding[^1] = 1;
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(padding, UniqueId, 8));
        var truncated = Identifier(40)[..^1];
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(truncated, UniqueId, 8));
        var wrongCount = Identifier(40);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongCount.AsSpan(8), 2);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(wrongCount, UniqueId, 8));
    }

    [Fact]
    public void NvmeReadAcceptsTheRealCountedEndShapeAndStillChecksOpenedObjectIdentity()
    {
        var query = new FakeQuery();
        var read = WindowsNvmeHealthReader.Read(7, "SERIAL-A", UniqueId, 8, _ => query);
        Assert.NotNull(read);
        Assert.Equal(302 - 273.15, read.Values[ResourceManager.App.Domain.Metrics.MetricFieldCode.NvmeCompositeTemperature]);
        Assert.Equal(new uint[] { 0, 2, 49 }, query.Properties);
        Assert.True(query.Disposed);
        var mismatch = new FakeQuery();
        Assert.Null(WindowsNvmeHealthReader.Read(7, "SERIAL-A", "eui.8877665544332211", 8, _ => mismatch));
        Assert.Equal(new uint[] { 0, 2 }, mismatch.Properties);
        Assert.True(mismatch.Disposed);
    }

    private static byte[] Identifier(ushort next)
    {
        var bytes = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 3); // UTF-8
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 8); // SCSI name string
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), next);
        Encoding.ASCII.GetBytes(UniqueId).CopyTo(bytes, 28);
        return bytes;
    }

    private sealed class FakeQuery : IWindowsNvmeHealthQuery
    {
        public List<uint> Properties { get; } = [];
        public bool Disposed { get; private set; }
        public byte[]? Query(byte[] input, int outputLength)
        {
            var property = BinaryPrimitives.ReadUInt32LittleEndian(input);
            Properties.Add(property);
            if (property == 2) return Identifier(40);
            if (property == 0)
            {
                var descriptor = new byte[64];
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 40);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 64);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(24), 40);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(28), 17);
                Encoding.ASCII.GetBytes("SERIAL-A").CopyTo(descriptor, 40);
                return descriptor;
            }
            Assert.Equal(49U, property);
            var response = (byte[])input.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(response, 48);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 48);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(49), 302);
            return response;
        }
        public void Dispose() => Disposed = true;
    }
}
