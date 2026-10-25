using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Shared.Runtime.TrustedAccess;

public sealed record SessionLaunchRequest(string ExecutablePath, string Arguments);
public sealed record SessionLaunchResult(uint ProcessId);

public static class SessionLaunchContract
{
    private static readonly SessionLaunchJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    public static string Encode(SessionLaunchRequest request)
    {
        Validate(request);
        return JsonSerializer.Serialize(request, Json.SessionLaunchRequest);
    }

    public static SessionLaunchRequest Decode(string payload)
    {
        if (payload.Length > 64 * 1024) throw new InvalidDataException("Session launch request exceeds its bound.");
        var request = JsonSerializer.Deserialize(payload, Json.SessionLaunchRequest)
            ?? throw new InvalidDataException("Missing session launch request.");
        Validate(request);
        return request;
    }

    public static string EncodeResult(uint processId)
        => JsonSerializer.Serialize(new SessionLaunchResult(processId), Json.SessionLaunchResult);

    public static SessionLaunchResult DecodeResult(string payload)
    {
        var result = JsonSerializer.Deserialize(payload, Json.SessionLaunchResult);
        return result is { ProcessId: > 0 } ? result : throw new InvalidDataException("Invalid session launch result.");
    }

    private static void Validate(SessionLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || request.ExecutablePath.Length > 32_000
            || request.ExecutablePath.Contains('\0') || request.ExecutablePath.Contains('"')
            || !Path.IsPathFullyQualified(request.ExecutablePath)
            || request.Arguments is null || request.Arguments.Contains('\0')
            || request.Arguments.Length > 24_000
            || request.ExecutablePath.Length + request.Arguments.Length + 4 > 32_767)
            throw new InvalidDataException("Invalid session launch command.");
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SessionLaunchRequest))]
[JsonSerializable(typeof(SessionLaunchResult))]
internal partial class SessionLaunchJsonContext : JsonSerializerContext;
