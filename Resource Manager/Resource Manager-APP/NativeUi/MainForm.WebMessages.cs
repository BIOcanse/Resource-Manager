using System.Text.Json;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private void HandleWebMessage(string json)
    {
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
                _ = HideToBackgroundAsync();
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
            case "host.backend-session.request":
                PublishBackendSessionProjection();
                break;
            case "shell.pickFolder":
                HandleFolderPickerRequest(message);
                break;
            case "shell.language":
                Localization.NativeUiText.Apply(
                    message.TryGetProperty("language", out var languageProperty)
                        && languageProperty.ValueKind == JsonValueKind.String
                        ? languageProperty.GetString()
                        : null);
                break;
        }
    }
}
