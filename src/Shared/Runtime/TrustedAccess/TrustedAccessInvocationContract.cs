using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Shared.Runtime.TrustedAccess;

public sealed record TrustedAccessInvocationRequest(string Operation, string Key, string Payload);

public sealed record TrustedAccessInvocationResponse(string Status, string? Payload);

/// <summary>
/// The single native invocation pipe contract. Each connection carries one bounded JSON
/// request and one bounded JSON response, both prefixed by a little-endian byte count.
/// </summary>
public static class TrustedAccessInvocationContract
{
    public const string PipeName = "ResourceManager.TrustedAccess.Invoke.v1";
    public const string GpuResolveOperation = "gpu.resolve";
    public const string GpuReportOperation = "gpu.report";
    public const string SessionLaunchOperation = "session.launch";
    public const string Success = "ok";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string Unavailable = "unavailable";
    public const string InvalidRequest = "invalid-request";
    public const int MaximumFrameBytes = 128 * 1024;
    public const int MaximumKeyBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly TrustedAccessInvocationJsonContext JsonContext = new(SerializerOptions);

    public static async Task WriteRequestAsync(
        Stream stream,
        TrustedAccessInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Operation)
            || request.Operation.Length > 64
            || request.Key is null
            || StrictUtf8.GetByteCount(request.Key) > MaximumKeyBytes
            || request.Payload is null)
        {
            throw new InvalidDataException("Invalid trusted access invocation request.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonContext.TrustedAccessInvocationRequest);
        if (bytes.Length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("Trusted access invocation request exceeds its bound.");
        }
        await WriteFrameAsync(stream, bytes, cancellationToken);
    }

    public static async Task<TrustedAccessInvocationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken);
        try
        {
            var request = JsonSerializer.Deserialize(
                bytes,
                JsonContext.TrustedAccessInvocationRequest)
                ?? throw new InvalidDataException("Missing trusted access invocation request.");
            if (string.IsNullOrWhiteSpace(request.Operation)
                || request.Operation.Length > 64
                || request.Key is null
                || request.Key.Length > MaximumKeyBytes
                || request.Payload is null
                || StrictUtf8.GetByteCount(request.Key) > MaximumKeyBytes)
            {
                throw new InvalidDataException("Invalid trusted access invocation request.");
            }
            return request;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid trusted access invocation JSON.", error);
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("Invalid trusted access invocation UTF-8.", error);
        }
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        TrustedAccessInvocationResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Status is not (Success or Unauthorized or Forbidden or Unavailable or InvalidRequest)
            || response.Payload is { } payload && StrictUtf8.GetByteCount(payload) > MaximumFrameBytes)
        {
            throw new InvalidDataException("Invalid trusted access invocation response.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.TrustedAccessInvocationResponse);
        if (bytes.Length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("Trusted access invocation response exceeds its bound.");
        }
        await WriteFrameAsync(stream, bytes, cancellationToken);
    }

    public static async Task<TrustedAccessInvocationResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken);
        try
        {
            var response = JsonSerializer.Deserialize(
                bytes,
                JsonContext.TrustedAccessInvocationResponse)
                ?? throw new InvalidDataException("Missing trusted access invocation response.");
            if (response.Status is not (Success or Unauthorized or Forbidden or Unavailable or InvalidRequest)
                || response.Payload is { } payload && StrictUtf8.GetByteCount(payload) > MaximumFrameBytes)
            {
                throw new InvalidDataException("Invalid trusted access invocation response.");
            }
            return response;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid trusted access invocation JSON.", error);
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("Invalid trusted access invocation frame length.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid trusted access invocation UTF-8.", error);
        }
        return bytes;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TrustedAccessInvocationRequest))]
[JsonSerializable(typeof(TrustedAccessInvocationResponse))]
internal partial class TrustedAccessInvocationJsonContext : JsonSerializerContext;
