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

    private const string MemoryCpuTierPath = "$.memoryCpuTier";

    private static JsonObject MemoryCpuTierSection(JsonNode? value)
    {
        if (value is not JsonObject section)
            throw new AppSettingsPatchException("invalid-settings-value-type", MemoryCpuTierPath);
        foreach (var property in section)
            if (property.Key is not ("tier" or "overrides"))
                throw new AppSettingsPatchException("invalid-settings-value-type", $"{MemoryCpuTierPath}.{property.Key}");
        if (section["tier"] is not JsonValue)
            throw new AppSettingsPatchException("invalid-settings-value-type", $"{MemoryCpuTierPath}.tier");
        if (section["overrides"] is not JsonObject)
            throw new AppSettingsPatchException("invalid-settings-value-type", $"{MemoryCpuTierPath}.overrides");
        return (JsonObject)section.DeepClone();
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

                // The memory/CPU tier section is replaced whole, not merged by key: the patch states the tier
                // and the complete overrides map, so removing an option's key clears that override. Inside
                // the map a null value means "not set"; the write gate accepts it only where the option allows it.
                if (propertyPath == MemoryCpuTierPath)
                {
                    item.Target[property.Key] = MemoryCpuTierSection(property.Value);
                    continue;
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
