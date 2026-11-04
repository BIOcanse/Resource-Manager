using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ResourceManager.App.Endpoints.Transport;

// H2 bridge: remove when C04 moves the sending host to Zig.
// The caller transfers a fully encoded final-output lease after synchronous source reads end.
// A source map, borrowed source memory, pin, or GCHandle must never cross this boundary.
internal static class DataAreaBinaryResponse
{
    internal static async ValueTask SendAsync(
        Stream destination,
        IMemoryOwner<byte> outputLease,
        int frameLength,
        int maximumFrameLength,
        CancellationToken cancellationToken)
    {
        using (outputLease)
        {
            ArgumentNullException.ThrowIfNull(outputLease);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameLength);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameLength);
            if (frameLength > maximumFrameLength)
                throw new ArgumentOutOfRangeException(nameof(frameLength));

            var output = outputLease.Memory;
            if (frameLength > output.Length)
                throw new ArgumentOutOfRangeException(nameof(frameLength));

            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(output.Slice(0, frameLength), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
