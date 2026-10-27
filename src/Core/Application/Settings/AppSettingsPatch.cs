using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public sealed record AppSettingsPatchRequest(
    string ExpectedRevision,
    JsonElement Changes);

public sealed class AppSettingsPatchException(string message, string? fieldPath = null) : IOException(message)
{
    public string? FieldPath { get; } = fieldPath;
}

public static class AppSettingsPatchApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new AppSettingsJsonContext(new(JsonSerializerDefaults.Web)).Options;

    public static AppSettings Apply(AppSettings current, JsonElement changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (changes.ValueKind != JsonValueKind.Object)
        {
            throw new AppSettingsPatchException(
                "invalid-settings-value-type", "$");
        }

        var target = JsonSerializer.SerializeToNode(current, JsonOptions) as JsonObject
            ?? throw new AppSettingsPatchException(
                "The current application settings could not be represented as an object.");
        var patch = JsonNode.Parse(changes.GetRawText()) as JsonObject
            ?? throw new AppSettingsPatchException(
                "Application settings changes must be a JSON object.");

        ApplyObjectPatch(target, patch);
        try
        {
            var candidate = target.Deserialize<AppSettings>(JsonOptions)
                ?? throw new AppSettingsPatchException("invalid-settings-value-type", "$");
            return AppSettingsNormalizer.Normalize(candidate);
        }
        catch (JsonException error)
        {
            throw new AppSettingsPatchException("invalid-settings-value-type", error.Path ?? "$");
        }
    }

    private static void ApplyObjectPatch(
        JsonObject target,
        JsonObject patch)
    {
        var pending = new Stack<(JsonObject Target, JsonObject Patch, string Path)>();
        pending.Push((target, patch, "$"));
        while (pending.TryPop(out var item))
        {
            foreach (var property in item.Patch)
            {
                var propertyPath = $"{item.Path}.{property.Key}";
                if (item.Path == "$"
                    && string.Equals(property.Key, "version", StringComparison.Ordinal))
                {
                    throw new AppSettingsPatchException(
                        "The application settings schema version cannot be patched.");
                }

                if (!item.Target.TryGetPropertyValue(property.Key, out var currentValue))
                {
                    throw new AppSettingsPatchException(
                        "invalid-settings-value-type", propertyPath);
                }

                if (property.Value is JsonObject patchObject)
                {
                    if (currentValue is not JsonObject targetObject)
                    {
                        throw new AppSettingsPatchException(
                            "invalid-settings-value-type", propertyPath);
                    }

                    pending.Push((targetObject, patchObject, propertyPath));
                    continue;
                }

                if (property.Value is null)
                {
                    throw new AppSettingsPatchException(
                        "invalid-settings-value-type", propertyPath);
                }

                item.Target[property.Key] = property.Value.DeepClone();
            }
        }
    }
}
