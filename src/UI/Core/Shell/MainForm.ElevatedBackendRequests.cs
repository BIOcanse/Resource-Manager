using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private int elevatedBackendRequestActive;

    // WebView2FrontendHost dispatches only messages from its active trusted top-level document.
    private async Task HandleElevatedBackendRequestAsync(JsonElement message)
    {
        if (!message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParse(id.GetString(), out _)) return;
        var ownsRequest = false;
        ElevatedBackendResponse? value = null;
        string? error = null;
        try
        {
            if (!backendContentEnabled || elevatedBackendRequest is null
                || Interlocked.CompareExchange(ref elevatedBackendRequestActive, 1, 0) != 0)
                throw new InvalidOperationException();
            ownsRequest = true;
            var mutation = new ElevatedBackendMutation(message.GetProperty("method").GetString()!,
                message.GetProperty("path").GetString()!, message.GetProperty("body").GetString()!);
            mutation.Validate();
            value = await elevatedBackendRequest(mutation);
            value.Validate();
        }
        catch (UacApprovalCanceledException) { error = "canceled"; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or JsonException or HttpRequestException
            or KeyNotFoundException or OperationCanceledException)
        { error = "unavailable"; }
        finally { if (ownsRequest) Interlocked.Exchange(ref elevatedBackendRequestActive, 0); }
        if (IsDisposed || !frontendHost.IsReady) return;
        try
        {
            frontendHost.PostJsonMessage(JsonSerializer.Serialize(new ElevatedBackendBridgeResponse(
                "backend.elevatedRequest.result", id.GetString()!, value?.StatusCode, value?.Body, error),
                ElevatedBackendBridgeJsonContext.Default.ElevatedBackendBridgeResponse));
        }
        catch (InvalidOperationException) { }
    }
}

internal sealed record ElevatedBackendBridgeResponse(string Type, string RequestId, int? StatusCode, string? Body, string? Error);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ElevatedBackendBridgeResponse))]
internal partial class ElevatedBackendBridgeJsonContext : JsonSerializerContext;
