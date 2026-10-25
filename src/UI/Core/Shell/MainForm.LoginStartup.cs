using System.Diagnostics;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using ResourceManager.NativeUi.SystemIntegration;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private void HandleLoginStartupRequest(JsonElement message, bool update)
    {
        if (!message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 and <= 128 } requestId) return;
        bool? enabled = null;
        string? error = null;
        try
        {
            var owner = new LoginStartupRegistration(Environment.ProcessPath
                ?? throw new InvalidOperationException("The frontend executable path is unavailable."));
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            if (update)
            {
                if (!message.TryGetProperty("enabled", out var value)
                    || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("A boolean startup value is required.");
                enabled = owner.Set(currentUser, value.GetBoolean());
            }
            else enabled = owner.Read(currentUser);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or ArgumentException)
        {
            error = update ? "write-failed" : "unavailable";
            Trace.TraceWarning("Frontend logon registration failed ({0}).", failure.GetType().Name);
        }
        if (!IsDisposed && frontendHost.IsReady)
            frontendHost.PostJsonMessage(JsonSerializer.Serialize(
                new LoginStartupResponse("shell.loginStartup.result", requestId, enabled, error),
                LoginStartupJsonContext.Default.LoginStartupResponse));
    }
}

internal sealed record LoginStartupResponse(string Type, string RequestId, bool? Enabled, string? Error);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LoginStartupResponse))]
internal partial class LoginStartupJsonContext : JsonSerializerContext;
