using System.Text.Json;

namespace ResourceManager.Shared.Packages;

public sealed class UpdatePackageIncompatibleException(string message) : Exception(message);

/// <summary>Release manifest data-format admission shared by preparation and package switching.</summary>
public static class UpdatePackageCompatibility
{
    public static void RequireCompatible(string packageRoot, string target, Func<string, int> readDatabaseSchema)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "release-manifest.json")));
        if (!document.RootElement.TryGetProperty("updateCompatibility", out var compatibility))
            throw new InvalidDataException("发行包未声明数据兼容范围，不能用于更新。");
        var minSettings = Version.Parse(compatibility.GetProperty("minimumSettingsSchema").GetString()!);
        var maxSettings = Version.Parse(compatibility.GetProperty("maximumSettingsSchema").GetString()!);
        var maxDatabase = compatibility.GetProperty("maximumDatabaseSchema").GetInt32();
        var settingsPath = Path.Combine(target, "Config", "app-settings.json");
        if (File.Exists(settingsPath))
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var current = Version.Parse(settings.RootElement.GetProperty("version").GetString()!);
            if (current < minSettings || current > maxSettings)
                throw new UpdatePackageIncompatibleException("现有设置格式不在目标发行版可迁移范围内。");
        }
        var databasePath = Path.Combine(target, "UserData", "Database", "resource-manager.db");
        if (File.Exists(databasePath))
        {
            var schema = readDatabaseSchema(databasePath);
            if (schema < 0 || schema > maxDatabase)
                throw new UpdatePackageIncompatibleException("现有数据库格式高于目标发行版支持范围。");
        }
    }
}
