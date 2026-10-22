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

    [Fact]
    public void Apply_RoundTripsTheReadingSourceModeAndNormalizesUnknownValues()
    {
        var current = AppSettingsDefaults.Create();
        using var all = JsonDocument.Parse("""{"appearance":{"readingSourceMode":"all"}}""");
        using var unknown = JsonDocument.Parse("""{"appearance":{"readingSourceMode":"everything"}}""");

        var patched = AppSettingsPatchApplier.Apply(current, all.RootElement);
        var normalized = AppSettingsPatchApplier.Apply(patched, unknown.RootElement);
        var persisted = JsonSerializer.SerializeToElement(patched, JsonSerializerOptions.Web)
            .GetProperty("appearance");

        Assert.Equal(AppReadingSourceModes.All, patched.Appearance.ReadingSourceMode);
        Assert.Equal("all", persisted.GetProperty("readingSourceMode").GetString());
        Assert.Equal(AppReadingSourceModes.Preferred, normalized.Appearance.ReadingSourceMode);
        Assert.Equal(current.Appearance.ByteUnitMode, patched.Appearance.ByteUnitMode);
    }

    [Fact]
    public void Apply_RoundTripsTheResourceBarFrameRateAndClampsIt()
    {
        var current = AppSettingsDefaults.Create();
        using var custom = JsonDocument.Parse(
            """{"appearance":{"resourceBarFrameRateMode":"custom","resourceBarFrameRate":90}}""");
        using var tooHigh = JsonDocument.Parse("""{"appearance":{"resourceBarFrameRate":500}}""");
        using var display = JsonDocument.Parse("""{"appearance":{"resourceBarFrameRateMode":"display"}}""");

        var patched = AppSettingsPatchApplier.Apply(current, custom.RootElement);
        var clamped = AppSettingsPatchApplier.Apply(patched, tooHigh.RootElement);
        var following = AppSettingsPatchApplier.Apply(clamped, display.RootElement);
        var persisted = JsonSerializer.SerializeToElement(patched, JsonSerializerOptions.Web)
            .GetProperty("appearance");

        Assert.Equal(
            (AppResourceBarFrameRateModes.Custom, 90),
            (patched.Appearance.ResourceBarFrameRateMode, patched.Appearance.ResourceBarFrameRate));
        Assert.Equal("custom", persisted.GetProperty("resourceBarFrameRateMode").GetString());
        Assert.Equal(90, persisted.GetProperty("resourceBarFrameRate").GetInt32());
        Assert.Equal(240, clamped.Appearance.ResourceBarFrameRate);
        Assert.Equal(
            (AppResourceBarFrameRateModes.Display, 240),
            (following.Appearance.ResourceBarFrameRateMode, following.Appearance.ResourceBarFrameRate));
        Assert.Equal(current.Appearance.Theme, following.Appearance.Theme);
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
