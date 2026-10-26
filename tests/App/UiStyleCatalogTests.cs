using ResourceManager.Shared.Appearance;

namespace Resource_Manager_APP.Tests;

public sealed class UiStyleCatalogTests
{
    [Theory]
    [InlineData(null, null, "standard", "system")]
    [InlineData("standard", "lowContrast", "standard", "lowContrast")]
    [InlineData("classic", "dark", "classic", "dark")]
    [InlineData("classic", "paper", "classic", "system")]
    [InlineData("soft", "paper", "soft", "system")]
    [InlineData("terminal", "ice", "terminal", "ice")]
    [InlineData("Terminal", "ice", "standard", "system")]
    public void Normalize_KeepsOnlyPalettesTheStyleOffers(
        string? uiStyle,
        string? theme,
        string expectedUiStyle,
        string expectedTheme)
    {
        Assert.Equal(new UiAppearance(expectedUiStyle, expectedTheme), UiStyleCatalog.Normalize(uiStyle, theme));
    }

    [Fact]
    public void ResolveStartupBackground_FollowsWindowsForSystemPalettes()
    {
        Assert.Equal(new UiStartupBackground(243, 243, 243), UiStyleCatalog.ResolveStartupBackground(null, null, systemUsesLightTheme: true));
        Assert.Equal(new UiStartupBackground(32, 32, 32), UiStyleCatalog.ResolveStartupBackground(null, null, systemUsesLightTheme: false));
        Assert.Equal(new UiStartupBackground(244, 247, 248), UiStyleCatalog.ResolveStartupBackground("classic", "system", systemUsesLightTheme: true));
        Assert.Equal(new UiStartupBackground(20, 22, 23), UiStyleCatalog.ResolveStartupBackground("classic", "system", systemUsesLightTheme: false));
        Assert.Equal(new UiStartupBackground(230, 233, 230), UiStyleCatalog.ResolveStartupBackground("standard", "lowContrast", systemUsesLightTheme: false));
        Assert.Equal(
            UiStyleCatalog.ResolveStartupBackground("outline", "paper", systemUsesLightTheme: false),
            UiStyleCatalog.ResolveStartupBackground("outline", "system", systemUsesLightTheme: true));
        Assert.Equal(
            UiStyleCatalog.ResolveStartupBackground("outline", "graphite", systemUsesLightTheme: true),
            UiStyleCatalog.ResolveStartupBackground("outline", "system", systemUsesLightTheme: false));
    }
}
