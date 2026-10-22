using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsPatchTests
{
    [Fact]
    public void Apply_ChangesOnlyAddressedLeaf()
    {
        var current = AppSettingsDefaults.Create();
        using var changes = JsonDocument.Parse(
            """
            {
              "appearance": {
                "theme": "dark"
              }
            }
            """);

        var patched = AppSettingsPatchApplier.Apply(current, changes.RootElement);

        Assert.Equal("dark", patched.Appearance.Theme);
        Assert.Equal(current.Appearance.Language, patched.Appearance.Language);
        Assert.Equal(
            JsonSerializer.Serialize(current.Performance),
            JsonSerializer.Serialize(patched.Performance));
        Assert.Equal(
            JsonSerializer.Serialize(current.SystemIntegration),
            JsonSerializer.Serialize(patched.SystemIntegration));
    }

    [Fact]
    public void Apply_SwitchingStyleKeepsAPaletteTheNewStyleOffers()
    {
        var current = AppSettingsDefaults.Create();
        using var keep = JsonDocument.Parse("""{"appearance":{"uiStyle":"soft","theme":"dark"}}""");
        using var fallback = JsonDocument.Parse("""{"appearance":{"uiStyle":"terminal"}}""");

        var soft = AppSettingsPatchApplier.Apply(current, keep.RootElement);
        var terminal = AppSettingsPatchApplier.Apply(soft, fallback.RootElement);

        Assert.Equal(("soft", "dark"), (soft.Appearance.UiStyle, soft.Appearance.Theme));
        Assert.Equal(("terminal", "phosphor"), (terminal.Appearance.UiStyle, terminal.Appearance.Theme));
    }

    [Theory]
    [InlineData("{\"version\":\"0.0.0\"}")]
    [InlineData("{\"appearance\":{\"unknownField\":true}}")]
    [InlineData("{\"appearance\":{\"theme\":null}}")]
    public void Apply_RejectsUnsafeOrUnknownChanges(string payload)
    {
        using var changes = JsonDocument.Parse(payload);

        Assert.Throws<AppSettingsPatchException>(() =>
            AppSettingsPatchApplier.Apply(
                AppSettingsDefaults.Create(),
                changes.RootElement));
    }
}
