using System.Globalization;
using System.Text.Json;

namespace ResourceManager.Shared.Appearance;

/// <summary>
/// 界面风格和配色目录。唯一来源是同目录的 ui-styles.json（嵌入资源），规则见 docs/ui_styles.md。
/// 后端设置归一化和 NativeUI 启动底色都从这里取。
/// </summary>
public static class UiStyleCatalog
{
    private static readonly Lazy<IReadOnlyList<StyleEntry>> Styles = new(Load);

    /// <summary>
    /// 风格不认识就用默认风格（目录第一个）；配色不在该风格里就用该风格的默认配色（该风格第一个）。
    /// </summary>
    public static UiAppearance Normalize(string? uiStyle, string? theme)
    {
        var style = Styles.Value.FirstOrDefault(candidate => string.Equals(candidate.Id, uiStyle, StringComparison.Ordinal))
            ?? Styles.Value[0];
        var palette = style.Palettes.FirstOrDefault(candidate => string.Equals(candidate.Id, theme, StringComparison.Ordinal))
            ?? style.Palettes[0];
        return new UiAppearance(style.Id, palette.Id);
    }

    /// <summary>
    /// 启动时窗口和 WebView 的底色，先按 <see cref="Normalize"/> 归一化；跟随系统的配色按 Windows 浅色/深色取它跟随的配色。
    /// </summary>
    public static UiStartupBackground ResolveStartupBackground(string? uiStyle, string? theme, bool systemUsesLightTheme)
    {
        var appearance = Normalize(uiStyle, theme);
        var style = Styles.Value.First(candidate => candidate.Id == appearance.UiStyle);
        var palette = style.Palettes.First(candidate => candidate.Id == appearance.Theme);
        if (palette.Follows is { } follows)
        {
            var followedId = systemUsesLightTheme ? follows.Light : follows.Dark;
            palette = style.Palettes.First(candidate => candidate.Id == followedId);
        }

        return palette.StartupBackground!.Value;
    }

    private static IReadOnlyList<StyleEntry> Load()
    {
        using var stream = typeof(UiStyleCatalog).Assembly.GetManifestResourceStream(
            "ResourceManager.Shared.Appearance.ui-styles.json")
            ?? throw new InvalidOperationException("Missing ui-styles.json.");
        using var document = JsonDocument.Parse(stream);
        var styles = new List<StyleEntry>();
        foreach (var style in document.RootElement.GetProperty("styles").EnumerateArray())
        {
            var palettes = new List<PaletteEntry>();
            foreach (var palette in style.GetProperty("palettes").EnumerateArray())
            {
                var follows = palette.TryGetProperty("follows", out var followsElement)
                    ? new FollowsEntry(
                        followsElement.GetProperty("light").GetString()!,
                        followsElement.GetProperty("dark").GetString()!)
                    : (FollowsEntry?)null;
                var background = palette.TryGetProperty("startupBackground", out var backgroundElement)
                    ? ParseBackground(backgroundElement.GetString()!)
                    : (UiStartupBackground?)null;
                palettes.Add(new PaletteEntry(palette.GetProperty("id").GetString()!, follows, background));
            }

            styles.Add(new StyleEntry(style.GetProperty("id").GetString()!, palettes));
        }

        return styles;
    }

    private static UiStartupBackground ParseBackground(string hex) =>
        new(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private sealed record StyleEntry(string Id, IReadOnlyList<PaletteEntry> Palettes);

    private sealed record PaletteEntry(string Id, FollowsEntry? Follows, UiStartupBackground? StartupBackground);

    private readonly record struct FollowsEntry(string Light, string Dark);
}

/// <summary>界面风格和该风格下的配色，均已归一化。</summary>
public readonly record struct UiAppearance(string UiStyle, string Theme);

public readonly record struct UiStartupBackground(byte Red, byte Green, byte Blue);
