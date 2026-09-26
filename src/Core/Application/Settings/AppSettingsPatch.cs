using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public sealed record AppSettingsPatchRequest(
    string ExpectedRevision,
    JsonElement Changes);

public sealed class AppSettingsPatchException(string message) : IOException(message);

public static class AppSettingsPatchApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AppSettings Apply(AppSettings current, JsonElement changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (changes.ValueKind != JsonValueKind.Object)
        {
            throw new AppSettingsPatchException(
                "Application settings changes must be a JSON object.");
        }

        var target = JsonSerializer.SerializeToNode(current, JsonOptions) as JsonObject
            ?? throw new AppSettingsPatchException(
                "The current application settings could not be represented as an object.");
        var patch = JsonNode.Parse(changes.GetRawText()) as JsonObject
            ?? throw new AppSettingsPatchException(
                "Application settings changes must be a JSON object.");

        ApplyObjectPatch(target, patch, path: string.Empty);
        var candidate = target.Deserialize<AppSettings>(JsonOptions)
            ?? throw new AppSettingsPatchException(
                "The patched application settings image is empty.");
        return AppSettingsNormalizer.Normalize(candidate);
    }

    private static void ApplyObjectPatch(
        JsonObject target,
        JsonObject patch,
        string path)
    {
        foreach (var property in patch)
        {
            var propertyPath = string.IsNullOrEmpty(path)
                ? property.Key
                : $"{path}.{property.Key}";
            if (string.IsNullOrEmpty(path)
                && string.Equals(property.Key, "version", StringComparison.Ordinal))
            {
                throw new AppSettingsPatchException(
                    "The application settings schema version cannot be patched.");
            }

            if (!target.TryGetPropertyValue(property.Key, out var currentValue))
            {
                throw new AppSettingsPatchException(
                    $"Unknown application settings field '{propertyPath}'.");
            }

            if (property.Value is JsonObject patchObject)
            {
                if (currentValue is not JsonObject targetObject)
                {
                    throw new AppSettingsPatchException(
                        $"Application settings field '{propertyPath}' is not an object.");
                }

                ApplyObjectPatch(targetObject, patchObject, propertyPath);
                continue;
            }

            if (property.Value is null)
            {
                throw new AppSettingsPatchException(
                    $"Application settings field '{propertyPath}' cannot be removed.");
            }

            target[property.Key] = property.Value.DeepClone();
        }
    }
}
