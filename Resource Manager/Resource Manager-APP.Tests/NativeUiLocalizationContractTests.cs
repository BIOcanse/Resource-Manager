using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResourceManager.NativeUi.Localization;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// NativeUi 界面语言合同：语言选择的唯一来源是持久化设置里的 appearance.language
/// （"system" 是它的正常取值），每个受支持语言都有一份完整、已翻译的文案包。
/// </summary>
public sealed class NativeUiLocalizationContractTests
{
    private static readonly Regex Chinese = new("[一-龥]", RegexOptions.Compiled);

    private static readonly PropertyInfo[] TextProperties = typeof(NativeText)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void EverySupportedLanguageHasItsOwnCompleteText()
    {
        Assert.NotEmpty(TextProperties);
        foreach (var language in NativeLanguage.SupportedIds)
        {
            var text = NativeTextCatalog.For(language);
            foreach (var property in TextProperties)
            {
                var value = Assert.IsType<string>(property.GetValue(text));
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{language} 的 {property.Name} 是空文案");
            }
        }
    }

    [Fact]
    public void NoTwoLanguagesShareTheSameTextInstance()
    {
        var seen = new Dictionary<NativeText, string>();
        foreach (var language in NativeLanguage.SupportedIds)
        {
            var text = NativeTextCatalog.For(language);
            Assert.False(
                seen.TryGetValue(text, out var owner),
                $"{language} 与 {owner} 共用同一份文案，说明缺少该语言的翻译");
            seen[text] = language;
        }
    }

    [Fact]
    public void NonHanScriptLanguagesContainNoLeftoverChineseText()
    {
        // 中文和日文本来就使用汉字，这里只查其余语言里残留的未翻译中文。
        foreach (var language in NativeLanguage.SupportedIds)
        {
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = NativeTextCatalog.For(language);
            foreach (var property in TextProperties)
            {
                var value = (string)property.GetValue(text)!;
                Assert.False(
                    Chinese.IsMatch(value),
                    $"{language} 的 {property.Name} 仍是中文：{value}");
            }
        }
    }

    [Fact]
    public void FormatTextsKeepTheirPlaceholder()
    {
        var formatProperties = TextProperties
            .Where(property => property.Name.EndsWith("Format", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(formatProperties);
        foreach (var language in NativeLanguage.SupportedIds)
        {
            var text = NativeTextCatalog.For(language);
            foreach (var property in formatProperties)
            {
                var value = (string)property.GetValue(text)!;
                Assert.Contains("{0}", value, StringComparison.Ordinal);
                _ = string.Format(CultureInfo.InvariantCulture, value, "x");
            }
        }
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("zh-TW", "zh-TW")]
    public void ConcreteSelectionResolvesToItself(string selection, string expected) =>
        Assert.Equal(expected, NativeLanguage.Resolve(selection));

    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("kl-GL")]
    public void SystemAndUnknownSelectionsResolveBySystemCandidates(string? selection)
    {
        Assert.Equal("ja-JP", NativeLanguage.Resolve(selection, ["ja"]));
        Assert.Equal("zh-TW", NativeLanguage.Resolve(selection, ["zh-Hant-HK"]));
        Assert.Equal(NativeLanguage.Fallback, NativeLanguage.Resolve(selection, ["kl-GL"]));
    }

    [Fact]
    public void PersistedLanguageComesFromAppearanceLanguage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rm-native-language-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                appearance = new { language = "de-DE" }
            }));
            Assert.Equal("de-DE", PersistedLanguageReader.Read(path));

            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                appearance = new { language = "system" }
            }));
            Assert.Equal(NativeLanguage.System, PersistedLanguageReader.Read(path));

            File.WriteAllText(path, "{ not json");
            Assert.Equal(NativeLanguage.System, PersistedLanguageReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingSettingsFileMeansFollowSystem() =>
        Assert.Equal(
            NativeLanguage.System,
            PersistedLanguageReader.Read(
                Path.Combine(Path.GetTempPath(), $"rm-absent-{Guid.NewGuid():N}.json")));
}
