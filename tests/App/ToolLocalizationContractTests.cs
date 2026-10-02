using System.Text.RegularExpressions;
using ResourceManager.Shared.Localization;

namespace Resource_Manager_APP.Tests;

public sealed class ToolLocalizationContractTests
{
    [Fact]
    public void EveryOfferedLanguageHasCompleteIndependentToolCopy()
    {
        var properties = typeof(ToolText).GetProperties().Where(property =>
            property.PropertyType == typeof(string) && property.Name != nameof(ToolText.Language)).ToArray();
        Assert.NotEmpty(properties);
        var english = ToolText.For("en-US");
        var texts = new HashSet<ToolText>(ReferenceEqualityComparer.Instance);
        foreach (var language in AppLanguage.SupportedIds)
        {
            var text = ToolText.For(language);
            Assert.Equal(language, text.Language);
            Assert.True(texts.Add(text), language);
            foreach (var property in properties)
            {
                var value = Assert.IsType<string>(property.GetValue(text));
                Assert.False(string.IsNullOrWhiteSpace(value), $"{language}.{property.Name}");
                if (!language.StartsWith("zh") && !language.StartsWith("ja"))
                    Assert.DoesNotMatch("[一-龥]", value);
                if (language != "en-US" && property.Name.EndsWith("Format"))
                {
                    var expected = Regex.Matches((string)property.GetValue(english)!, @"\{\d+\}").Select(m => m.Value).Order().ToArray();
                    var actual = Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order().ToArray();
                    Assert.Equal(expected, actual);
                }
                if (property.Name.EndsWith("Format"))
                    _ = text.Format(value, "SOURCE-TOKEN", "TARGET-TOKEN");
            }
            if (language != "en-US")
            {
                Assert.NotEqual(english.PackageActions, text.PackageActions);
                Assert.NotEqual(english.InstallConsent, text.InstallConsent);
                Assert.NotEqual(english.RecoveryActions, text.RecoveryActions);
            }
            Assert.Contains("HKLM", text.InstallRegistry);
            Assert.Contains("App Paths", text.InstallRegistry);
            Assert.Contains("LocalSystem", text.InstallService);
            Assert.Contains("release-manifest.json", text.PackagePlaceholder);
            Assert.Contains("SOURCE-TOKEN", text.Format(text.UpdateSummaryFormat, "SOURCE-TOKEN", "TARGET-TOKEN"));
            Assert.Contains("TARGET-TOKEN", text.Format(text.UpdateSummaryFormat, "SOURCE-TOKEN", "TARGET-TOKEN"));
        }
        var traditional = ToolText.For("zh-TW");
        Assert.Contains("資料夾", traditional.PackagePlaceholder);
        Assert.DoesNotContain("管理员", traditional.PackageActions);
    }

    [Fact]
    public void InstalledToolsReadTheSameLanguagePreferenceAsTheShell()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-tool-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Config", "app-settings.json"), "{\"appearance\":{\"language\":\"DE_de\"}}");
            Assert.Equal("de-DE", ToolText.FromInstallRoot(root).Language);
            Assert.Equal("de-DE", AppLanguage.Resolve("DE_de"));
            File.WriteAllText(Path.Combine(root, "Config", "app-settings.json"), "{invalid json");
            Assert.Equal(AppLanguage.System, AppLanguage.ReadPreference(Path.Combine(root, "Config", "app-settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
