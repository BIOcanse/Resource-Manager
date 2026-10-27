using System.Text.Json;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private void HandleWebMessage(string json)
    {
        if (frontendStopping) return;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                HandleStructuredWebMessage(document.RootElement);
                return;
            }

            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            HandleCommandWebMessage(document.RootElement.GetString());
        }
    }

    private void HandleCommandWebMessage(string? message)
    {
        switch (message)
        {
            case "window.drag":
                BeginDragMove();
                break;
            case "window.minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "window.maximize":
                ToggleMaximized();
                break;
            case "window.close":
                // Closing the window closes the frontend; the tray keeps running.
                // Hide synchronously; WebView2 must be disposed outside its callback.
                Hide();
                BeginInvoke(Close);
                break;
            case "update.applyExit":
                Hide();
                BeginInvoke(() => ExitRequested?.Invoke(this, EventArgs.Empty));
                break;
            case "taskManager.shortcutReplacement:on":
                taskManagerShortcutReplacementChanged(true);
                break;
            case "taskManager.shortcutReplacement:off":
                taskManagerShortcutReplacementChanged(false);
                break;
            case "editableHotkeys:reload":
                editableHotkeysChanged();
                break;
        }
    }

    private void HandleStructuredWebMessage(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        switch (typeProperty.GetString())
        {
            case "preferences.get":
            case "preferences.save":
            case "preferences.reset":
                HandlePreferencesRequest(message, typeProperty.GetString()!);
                break;
            case "host.backend-session.request":
                PublishBackendSessionProjection();
                break;
            case "shell.pickFolder":
                HandleFolderPickerRequest(message);
                break;
            case "shell.loginStartup.get":
            case "shell.loginStartup.set":
                HandleLoginStartupRequest(message, typeProperty.GetString() == "shell.loginStartup.set");
                break;
            case "shell.processAction":
                TrackFrontendRequest(HandleProcessActionRequestAsync(message.Clone()));
                break;
            case "backend.elevatedRequest":
                TrackFrontendRequest(HandleElevatedBackendRequestAsync(message.Clone()));
                break;
            case "shell.language":
                Localization.NativeUiText.Apply(
                    message.TryGetProperty("language", out var languageProperty)
                        && languageProperty.ValueKind == JsonValueKind.String
                        ? languageProperty.GetString()
                        : null);
                break;
            case "shell.animationStyle":
                if (message.TryGetProperty("style", out var styleProperty)
                    && styleProperty.ValueKind == JsonValueKind.String)
                    ApplyAnimationStyle(styleProperty.GetString());
                break;
        }
    }
}
