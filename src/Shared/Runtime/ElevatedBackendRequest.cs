using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ResourceManager.Shared.Runtime;

// Command-line metadata only. The mutation is delivered on the attested owned pipe.
public sealed record ElevatedBackendInvocation(string OperationId, int ParentProcessId, long ParentCreatedUtcTicks,
    int ServiceProcessId, long ServiceCreatedUtcTicks)
{
    public const string Argument = "--elevated-backend-request";
    [JsonIgnore] public string PipeName => "ResourceManager.BackendRequest." + OperationId;

    public void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "N", out _) || ParentProcessId <= 0 || ParentCreatedUtcTicks <= 0
            || ServiceProcessId <= 0 || ServiceCreatedUtcTicks <= 0)
            throw new InvalidDataException("Invalid elevated invocation binding.");
    }
}

public sealed record ElevatedBackendMutation(string Method, string Path, string Body)
{
    public void Validate()
    {
        if (Method is not ("POST" or "PUT" or "PATCH" or "DELETE") || string.IsNullOrEmpty(Path)
            || Path.Length > 2048 || !Path.StartsWith("/api/", StringComparison.Ordinal)
            || Path.Contains('?') || Path.Contains('#') || Path.Contains('\\')
            || Path.Any(char.IsControl) || Path.Contains("//", StringComparison.Ordinal)
            || Path.Split('/').Any(UnsafeSegment)
            || Body is null || ElevatedBackendRequestWire.StrictUtf8.GetByteCount(Body) > ElevatedBackendRequestWire.MaximumBodyBytes)
            throw new InvalidDataException("Invalid elevated backend mutation.");
        using var json = JsonDocument.Parse(Body);
    }

    private static bool UnsafeSegment(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
            if (segment[index] == '%' && (index + 2 >= segment.Length
                || !Uri.IsHexDigit(segment[index + 1]) || !Uri.IsHexDigit(segment[index + 2]))) return true;
        var decoded = Uri.UnescapeDataString(segment);
        return decoded is "." or ".." || decoded.Any(char.IsControl)
            || decoded.IndexOfAny(['/', '\\', '?', '#']) >= 0;
    }
}

public sealed record ElevatedBackendResponse(int StatusCode, string Body)
{
    public void Validate()
    {
        if (StatusCode is < 100 or > 599 || Body is null
            || ElevatedBackendRequestWire.StrictUtf8.GetByteCount(Body) > ElevatedBackendRequestWire.MaximumResponseBytes)
            throw new InvalidDataException("Invalid elevated backend response.");
        if (Body.Length > 0) { using var json = JsonDocument.Parse(Body); }
    }
}

public static class ElevatedBackendRequestWire
{
    public const int MaximumBodyBytes = 128 * 1024;
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumFrameBytes = MaximumResponseBytes * 2 + 4096;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly ElevatedBackendRequestJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    public static string EncodeInvocation(ElevatedBackendInvocation invocation)
    {
        invocation.Validate();
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invocation, Json.ElevatedBackendInvocation));
    }

    public static ElevatedBackendInvocation DecodeInvocation(string encoded)
    {
        if (encoded.Length > 4096) throw new InvalidDataException("Elevated invocation exceeds its metadata bound.");
        var result = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), Json.ElevatedBackendInvocation)
            ?? throw new InvalidDataException("Missing elevated invocation.");
        result.Validate();
        return result;
    }

    public static Task WriteMutationAsync(Stream stream, ElevatedBackendMutation mutation, CancellationToken token)
    {
        mutation.Validate();
        return WriteAsync(stream, mutation, Json.ElevatedBackendMutation, token);
    }

    public static async Task<ElevatedBackendMutation> ReadMutationAsync(Stream stream, CancellationToken token)
    {
        var mutation = await ReadAsync(stream, Json.ElevatedBackendMutation, token);
        mutation.Validate();
        return mutation;
    }

    public static Task WriteResponseAsync(Stream stream, ElevatedBackendResponse response, CancellationToken token)
    {
        response.Validate();
        return WriteAsync(stream, response, Json.ElevatedBackendResponse, token);
    }

    public static async Task<ElevatedBackendResponse> ReadResponseAsync(Stream stream, CancellationToken token)
    {
        var response = await ReadAsync(stream, Json.ElevatedBackendResponse, token);
        response.Validate();
        return response;
    }

    private static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> metadata, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, metadata);
        if (bytes.Length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Elevated frame exceeds its bound.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> metadata, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var count = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (count is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Elevated frame exceeds its bound.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token);
        _ = StrictUtf8.GetCharCount(bytes);
        return JsonSerializer.Deserialize(bytes, metadata) ?? throw new InvalidDataException("Missing elevated frame value.");
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ElevatedBackendInvocation))]
[JsonSerializable(typeof(ElevatedBackendMutation))]
[JsonSerializable(typeof(ElevatedBackendResponse))]
internal partial class ElevatedBackendRequestJsonContext : JsonSerializerContext;
