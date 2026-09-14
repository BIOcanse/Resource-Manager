using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal static class GpuApiObservationProtocol
{
    internal const int ByteLength = 16;
    internal const uint Version = 1;
    internal const GpuGraphicsApi AllApis = GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12
        | GpuGraphicsApi.Vulkan | GpuGraphicsApi.OpenGL | GpuGraphicsApi.D3D9;

    internal static byte[] EncodeRequest(GpuGraphicsApi apis, int durationMilliseconds)
    {
        if (apis == 0 || (apis & ~AllApis) != 0) throw new ArgumentOutOfRangeException(nameof(apis));
        if (durationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        var buffer = CreateSnapshotBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)apis);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), checked((uint)durationMilliseconds));
        return buffer;
    }

    internal static byte[] CreateSnapshotBuffer()
    {
        var buffer = new byte[ByteLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ByteLength);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), Version);
        return buffer;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> buffer, GpuGraphicsApi requested,
        out GpuApiObservationSnapshot snapshot)
    {
        snapshot = default;
        if (requested == 0 || (requested & ~AllApis) != 0 || buffer.Length != ByteLength
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != ByteLength
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]) != Version) return false;
        var observed = (GpuGraphicsApi)BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        var recording = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]);
        if ((observed & ~requested) != 0 || recording > 1) return false;
        snapshot = new(observed, recording == 1);
        return true;
    }
}

internal readonly record struct GpuApiObservationSnapshot(GpuGraphicsApi Apis, bool Recording);
