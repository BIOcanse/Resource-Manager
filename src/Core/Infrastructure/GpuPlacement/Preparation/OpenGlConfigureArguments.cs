using System.Buffers.Binary;

using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

internal static class OpenGlConfigureArguments
{
    // Matches the existing native Provider's policy-path storage, including its terminator.
    internal const int PolicyPathCapacity = 2048;
    internal const int HeaderBytes = 8;

    internal static byte[] Encode(string policyPath, PreparedOpenGlCallbacks source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyPath);
        var path = Path.GetFullPath(policyPath);
        if (path.Length >= PolicyPathCapacity)
            throw new ArgumentOutOfRangeException(nameof(policyPath), "The Provider policy path does not fit.");

        var sourceOffset = checked(HeaderBytes + path.Length * 2);
        var bytes = new byte[checked(sourceOffset + source.Bytes.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)path.Length));
        for (var i = 0; i < path.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(HeaderBytes + i * 2), path[i]);
        source.Bytes.Span.CopyTo(bytes.AsSpan(sourceOffset));
        return bytes;
    }
}
