using System.Buffers.Binary;
using System.Text;

namespace ResourceManager.Shared.Runtime;

// Operations on the existing per-user session pipe. Frontend credential operation remains 1.
public static class GpuLaunchSessionContract
{
    public const byte ResolveOperation = 2;
    public const byte ReportOperation = 3;
    public const int MaximumPayloadBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumPayloadBytes) throw new InvalidDataException("Invalid GPU launch session frame.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid GPU launch session UTF-8.", error); }
    }

    public static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length <= 0 || bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("Invalid GPU launch session frame.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
