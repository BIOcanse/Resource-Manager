using System.Text.Json;

namespace ResourceManager.NativeUi.Localization;

/// <summary>
/// NativeUi 启动时的语言来源：读持久化设置里的 <c>appearance.language</c>。
/// 该字段本身就有 "system" 这个合法取值，所以「跟随系统」是配置里的一个正常值，
/// 由 <see cref="NativeLanguage.Resolve"/> 统一解析；NativeUi 不维护第二套开关，也不写回设置。
/// </summary>
internal static class PersistedLanguageReader
{
    /// <summary>读取持久化的语言选择；文件缺失或不可读时返回 "system"。</summary>
    public static string Read(string? settingsPath = null)
    {
        var path = settingsPath ?? NativeUiPaths.AppSettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                return NativeLanguage.System;
            }

            // 走仓库统一的设置读取口径：后端原子替换设置文件时读取方保留完整旧映像。
            using var stream = Configuration.SettingsFileReader.Open(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("appearance", out var appearance)
                || appearance.ValueKind != JsonValueKind.Object
                || !appearance.TryGetProperty("language", out var language)
                || language.ValueKind != JsonValueKind.String)
            {
                return NativeLanguage.System;
            }

            return NativeLanguage.Normalize(language.GetString());
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return NativeLanguage.System;
        }
    }
}
