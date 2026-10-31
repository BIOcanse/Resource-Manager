using System.Diagnostics;
using System.Text.Json;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private void HandlePreferencesRequest(JsonElement message, string operation)
    {
        if (!message.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String)
            return;
        var response = new Dictionary<string, object?>
        {
            ["type"] = "preferences.result", ["requestId"] = requestId.GetString()
        };
        try
        {
            var owner = FrontendPreferencesOwner.Current;
            response["value"] = operation switch
            {
                "preferences.get" => owner.Read(),
                "preferences.reset" => owner.Reset(),
                "preferences.save" => SavePreferenceScope(owner, message),
                _ => throw new InvalidOperationException("Unknown frontend cache operation.")
            };
            if (operation is "preferences.save" or "preferences.reset")
                PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Trace.TraceError("Frontend preferences operation {0} failed: {1}", operation, error);
            response["error"] = "preferences-unavailable";
        }
        if (!IsDisposed && frontendHost.IsReady)
            frontendHost.PostJsonMessage(JsonSerializer.Serialize(response, FrontendPreferencesOwner.Json));
    }

    private static FrontendPreferences SavePreferenceScope(FrontendPreferencesOwner owner, JsonElement message)
    {
        var value = message.GetProperty("value").Deserialize<FrontendPreferences>(FrontendPreferencesOwner.Json)
            ?? throw new InvalidDataException("Missing frontend cache value.");
        var scope = message.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() : null;
        return scope switch
        {
            "global" => owner.SaveGlobal(value),
            "dashboard" => owner.SaveDashboard(value),
            "targetedReportViewTemplates" => owner.SaveTargetedReportViewTemplates(value),
            null => owner.Save(value),
            _ => throw new InvalidDataException("Invalid frontend cache update scope.")
        };
    }
}
