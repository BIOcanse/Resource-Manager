using System.Buffers.Binary;
using System.Text.Json;

namespace ResourceManager.Shared.Runtime;

public static class FrontendSessionCredentialContract
{
    public const byte ProtocolVersion = 1;
    public const string ProductId = "ResourceManager.FrontendSession";
    public const int MaximumReplyBytes = 4096;
    public const int AccessTokenHexLength = 64;
    public static string PipeName(uint sessionId) => $"ResourceManager.Service.Session.{sessionId}.v1";

    public static async Task WriteAsync(Stream stream, FrontendSessionCredentialReply reply, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(reply);
        if (data.Length > MaximumReplyBytes) throw new InvalidDataException("Session reply exceeds its bound.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<FrontendSessionCredentialReply> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumReplyBytes) throw new InvalidDataException("Invalid session reply size.");
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return JsonSerializer.Deserialize<FrontendSessionCredentialReply>(data)
            ?? throw new InvalidDataException("Missing session reply.");
    }
}

public sealed record FrontendSessionCredentialReply(string ProductId, int ProtocolVersion,
    ulong ServiceInstanceId, int ServiceProcessId, long ServiceProcessCreatedUtcTicks,
    int UiProcessId, long UiProcessCreatedUtcTicks, int WindowsSessionId, string AccessToken);
