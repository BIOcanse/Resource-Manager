using System.Text.Json;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private async Task HandleProcessActionRequestAsync(JsonElement message)
    {
        if (!message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParse(id.GetString(), out _)) return;
        var response = new Dictionary<string, object?>
        { ["type"] = "shell.processAction.result", ["requestId"] = id.GetString() };
        try
        {
            if (!backendContentEnabled || elevatedProcessAction is null
                || !message.TryGetProperty("batch", out var batchJson)) throw new InvalidOperationException();
            var batch = batchJson.Deserialize<ProcessActionBatch>(ElevatedProcessActionRequest.JsonOptions)
                ?? throw new InvalidDataException();
            batch.Validate();
            var result = await elevatedProcessAction(batch);
            using var payload = JsonDocument.Parse(result);
            response["result"] = payload.RootElement.Clone();
        }
        catch (UacApprovalCanceledException) { response["canceled"] = true; }
        catch (OperationCanceledException) { response["timedOut"] = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or JsonException or HttpRequestException)
        { response["error"] = "process-action-unavailable"; }
        if (IsDisposed || !frontendHost.IsReady) return;
        try { frontendHost.PostJsonMessage(JsonSerializer.Serialize(response)); }
        catch (InvalidOperationException) { }
    }
}
