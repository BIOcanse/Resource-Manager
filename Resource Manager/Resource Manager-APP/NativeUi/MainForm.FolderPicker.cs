using ResourceManager.NativeUi.Localization;
using System.Text.Json;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private void HandleFolderPickerRequest(JsonElement message)
    {
        var requestId = message.TryGetProperty("requestId", out var requestIdProperty)
            ? requestIdProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var title = message.TryGetProperty("title", out var titleProperty)
            ? titleProperty.GetString()
            : null;
        var initialPath = message.TryGetProperty("initialPath", out var initialPathProperty)
            ? initialPathProperty.GetString()
            : null;

        using var picker = new FolderBrowserDialog
        {
            Description = string.IsNullOrWhiteSpace(title) ? NativeUiText.Current.SelectSoftwareRootFolder : title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(initialPath) ? initialPath : string.Empty
        };
        var result = picker.ShowDialog(this);
        var response = new Dictionary<string, object?>
        {
            ["type"] = "shell.pickFolder.result",
            ["requestId"] = requestId,
            ["path"] = result == DialogResult.OK ? picker.SelectedPath : null
        };

        try
        {
            frontendHost.PostJsonMessage(JsonSerializer.Serialize(response));
        }
        catch (InvalidOperationException)
        {
        }
    }
}
