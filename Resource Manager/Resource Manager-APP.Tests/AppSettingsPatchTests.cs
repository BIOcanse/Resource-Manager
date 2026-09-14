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

        Assert.Equal(AppThemeModes.Dark, patched.Appearance.Theme);
        Assert.Equal(current.Appearance.Language, patched.Appearance.Language);
        Assert.Equal(
            JsonSerializer.Serialize(current.Performance),
            JsonSerializer.Serialize(patched.Performance));
        Assert.Equal(
            JsonSerializer.Serialize(current.SystemIntegration),
            JsonSerializer.Serialize(patched.SystemIntegration));
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
