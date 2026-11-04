using ResourceManager.Shared.Localization;

namespace ResourceManager.NativeUi.Localization;

/// <summary>Native shell compatibility entry point for the shared language resolver.</summary>
internal static class NativeLanguage
{
    public const string System = AppLanguage.System;
    public const string Fallback = AppLanguage.Fallback;
    public static IReadOnlyList<string> SupportedIds => AppLanguage.SupportedIds;
    public static string Normalize(string? language) => AppLanguage.Normalize(language);
    public static string Resolve(string? language, IEnumerable<string>? systemCandidates = null) =>
        AppLanguage.Resolve(language, systemCandidates);
    public static bool IsRightToLeft(string language) => AppLanguage.IsRightToLeft(language);
}
