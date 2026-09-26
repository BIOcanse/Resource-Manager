using System.Buffers.Binary;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionProtocolTests
{
    private static GpuWindowActionRequest Request() => new(1000, 134332000000000000, 12345, GpuWindowActionMethod.Resize);
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static byte[] Prepared()
    {
        var data = new byte[56];
        GpuWindowActionProtocol.Prepare(Request()).CopyTo(data, 0);
        Put(data, 4, 2); Put(data, 32, 111); Put(data, 44, 80); Put(data, 48, 60); Put(data, 52, 1);
        return data;
    }
    private static byte[] Completed()
    {
        var data = new byte[80];
        Put(data, 0, 2); Put(data, 4, 5); Put(data, 8, 1); Put(data, 16, 1);
        Put(data, 32, 81); Put(data, 36, 60); Put(data, 40, 1);
        Put(data, 44, 1); Put(data, 52, 1); Put(data, 68, 80); Put(data, 72, 60); Put(data, 76, 1);
        return data;
    }

    [Fact]
    public void ExactMessagesProduceTypedObservations()
    {
        var prepared = GpuWindowActionProtocol.ReadPreparation(Prepared(), Request());
        var done = GpuWindowActionProtocol.ReadCompletion(Completed(), GpuWindowActionMethod.Resize);
        Assert.Equal(80, prepared.Before.Width);
        Assert.Equal(81, done.AfterChange.Value!.Width);
        Assert.Equal(prepared.Before, done.AfterRestore.Value);
        Assert.Equal(GpuWindowCallState.Accepted, done.Restore.State);
    }

    [Theory]
    [InlineData(0, 1U)]
    [InlineData(4, 99U)]
    [InlineData(8, 1001U)]
    [InlineData(12, 1U)]
    [InlineData(20, 999U)]
    [InlineData(28, 1U)]
    [InlineData(32, 0U)]
    [InlineData(44, 0U)]
    [InlineData(52, 0U)]
    [InlineData(52, 9U)]
    public void PreparationRejectsMismatchedOrIneligibleRawFields(int offset, uint value)
    {
        var data = Prepared(); Put(data, offset, value);
        Assert.Throws<InvalidDataException>(() => GpuWindowActionProtocol.ReadPreparation(data, Request()));
    }

    [Theory]
    [InlineData(8, 0U)]
    [InlineData(8, 5U)]
    [InlineData(12, 5U)]
    [InlineData(16, 0U)]
    [InlineData(16, 2U)]
    [InlineData(20, 5U)]
    [InlineData(40, 8U)]
    [InlineData(44, 0U)]
    [InlineData(52, 0U)]
    public void CompletionRejectsContradictoryOrUnknownObservations(int offset, uint value)
    {
        var data = Completed(); Put(data, offset, value);
        Assert.Throws<InvalidDataException>(() => GpuWindowActionProtocol.ReadCompletion(data, GpuWindowActionMethod.Resize));
    }

    [Fact]
    public void AResizeResultCannotBeReinterpretedAsRedraw()
        => Assert.Throws<InvalidDataException>(() => GpuWindowActionProtocol.ReadCompletion(Completed(), GpuWindowActionMethod.Redraw));

    [Theory]
    [InlineData(7)]
    [InlineData(55)]
    [InlineData(57)]
    public void MessageLengthMustMatchExactly(int length)
    {
        var data = new byte[length]; Prepared().AsSpan(0, Math.Min(length, 56)).CopyTo(data);
        Assert.Throws<InvalidDataException>(() => GpuWindowActionProtocol.ReadPreparation(data, Request()));
    }

    [Fact]
    public void ParentMessageIsFixedAndDoesNotChangeTheLedgerFormat()
    {
        var message = GpuWindowActionProtocol.Parent(1234);
        Assert.Equal(16, message.Length);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(message));
        Assert.Equal(GpuWindowActionProtocol.Kind.Parent, GpuWindowActionProtocol.ReadKind(message));
        Assert.Equal(1234UL, BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(8)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuWindowActionProtocol.Parent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuWindowActionProtocol.Parent(ulong.MaxValue));
    }
}
