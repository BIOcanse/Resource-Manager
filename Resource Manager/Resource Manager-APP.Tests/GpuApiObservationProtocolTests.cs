using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuApiObservationProtocolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(3)]
    [InlineData(31)]
    public void ExplicitRequestMatchesNativeSixteenByteLayout(int apis)
    {
        var actual = GpuApiObservationProtocol.EncodeRequest((GpuGraphicsApi)apis, 5000);
        Assert.Equal(new byte[] { 16, 0, 0, 0, 1, 0, 0, 0, (byte)apis, 0, 0, 0, 0x88, 0x13, 0, 0 }, actual);
    }

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(32, 5000)]
    [InlineData(33, 5000)]
    [InlineData(-1, 5000)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void InvalidRequestIsRejectedBeforeRemoteAllocation(int apis, int duration)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GpuApiObservationProtocol.EncodeRequest((GpuGraphicsApi)apis, duration));

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(31, true)]
    public void CurrentApiSetAndRecordingAreDecodedWithoutInventingAnApi(int apis, bool recording)
    {
        var buffer = GpuApiObservationProtocol.CreateSnapshotBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)apis);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), recording ? 1u : 0u);
        Assert.True(GpuApiObservationProtocol.TryDecode(buffer, GpuApiObservationProtocol.AllApis, out var value));
        Assert.Equal((GpuGraphicsApi)apis, value.Apis);
        Assert.Equal(recording, value.Recording);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(0, 17)]
    [InlineData(4, 0)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(8, 32)]
    [InlineData(12, 2)]
    [InlineData(12, uint.MaxValue)]
    public void MalformedOrOutsideRequestedSetDoesNotPublishAResult(int offset, uint value)
    {
        var buffer = GpuApiObservationProtocol.CreateSnapshotBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
        Assert.False(GpuApiObservationProtocol.TryDecode(buffer, GpuGraphicsApi.D3D11, out var result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void WrongBufferLengthDoesNotDecode(int length)
        => Assert.False(GpuApiObservationProtocol.TryDecode(new byte[length], GpuGraphicsApi.D3D11, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public void InvalidRequestedSetDoesNotDecode(int requested)
        => Assert.False(GpuApiObservationProtocol.TryDecode(GpuApiObservationProtocol.CreateSnapshotBuffer(), (GpuGraphicsApi)requested, out _));
}
