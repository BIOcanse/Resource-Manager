using System.Globalization;
using System.Text.Json;

namespace ResourceManager.Shared.Localization;

/// <summary>
/// 语言 id 的归一化与解析。语义与前端 <c>i18n/settingsLanguages.ts</c> 的
/// <c>normalizeLanguageMode</c> / <c>resolveLanguageMode</c> 一致：
/// "system"、缺失、未知值都按系统语言候选解析，解析不到落到 zh-CN。
/// </summary>
public static class AppLanguage
{
    public const string System = "system";

    public const string Fallback = "zh-CN";

    /// <summary>Delivered languages, shared by all native entry points.</summary>
    public static readonly IReadOnlyList<string> SupportedIds =
    [
        "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
    ];

    private static readonly HashSet<string> SupportedIdSet =
        new(SupportedIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>把任意输入归一化为「system」或一个受支持的具体语言 id。</summary>
    public static string Normalize(string? language)
    {
        var value = (language ?? string.Empty).Trim().Replace('_', '-');
        if (value.Length == 0)
        {
            return System;
        }

        return SupportedIdSet.TryGetValue(value, out var canonical) ? canonical : System;
    }

    /// <summary>把语言选择解析为唯一的具体语言 id。</summary>
    public static string Resolve(string? language, IEnumerable<string>? systemCandidates = null)
    {
        var normalized = Normalize(language);
        if (!string.Equals(normalized, System, StringComparison.Ordinal))
        {
            return normalized;
        }

        foreach (var candidate in systemCandidates ?? SystemCandidates())
        {
            var matched = Match(candidate);
            if (matched is not null)
            {
                return matched;
            }
        }

        return Fallback;
    }

    public static bool IsRightToLeft(string language) =>
        string.Equals(language, "ar-SA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(language, "he-IL", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SystemCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var culture = CultureInfo.CurrentUICulture;
        var depth = 0;
        while (culture is not null && !string.IsNullOrEmpty(culture.Name) && depth < 8)
        {
            if (seen.Add(culture.Name))
            {
                yield return culture.Name;
            }

            var parent = culture.Parent;
            culture = ReferenceEquals(parent, culture) ? null : parent;
            depth++;
        }

        var installed = CultureInfo.InstalledUICulture.Name;
        if (!string.IsNullOrEmpty(installed) && seen.Add(installed))
        {
            yield return installed;
        }
    }

    /// <summary>Read the existing appearance.language preference without changing settings.</summary>
    public static string ReadPreference(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("appearance", out var appearance)
                && appearance.ValueKind == JsonValueKind.Object
                && appearance.TryGetProperty("language", out var language)
                && language.ValueKind == JsonValueKind.String
                    ? Normalize(language.GetString()) : System;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return System;
        }
    }

    private static string? Match(string language)
    {
        var normalized = language.Replace('_', '-').Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (SupportedIdSet.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        var separator = normalized.IndexOf('-');
        var prefix = (separator < 0 ? normalized : normalized[..separator]).ToLowerInvariant();
        return prefix switch
        {
            "zh" => normalized.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW"
                    : "zh-CN",
            "en" => "en-US",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "fr" => "fr-FR",
            "de" => "de-DE",
            "es" => "es-ES",
            "ru" => "ru-RU",
            // 这些语言的文案已经写好但尚未交付（见 SupportedIds）：系统语言是它们时落到英文基底，
            // 而不是中文，避免给非中文用户显示中文。放开某个语言时把它移到上面即可。
            "pt" or "uk" or "pl" or "tr" or "it" or "nl" or "sv" or "fi" or "da"
                or "nb" or "no" or "nn" or "cs" or "hu" or "ro" or "el" or "he" or "iw"
                or "ar" or "hi" or "id" or "in" or "vi" or "th" => "en-US",
            _ => null
        };
    }
}
