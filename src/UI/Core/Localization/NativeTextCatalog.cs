using ResourceManager.NativeUi.Localization.Texts;

namespace ResourceManager.NativeUi.Localization;

/// <summary>
/// 语言 id 到完整文案包的映射。每个受支持的语言都有一份自己的完整文案，
/// 这里不做基底合并，也没有隐式回退：只有解析不出受支持语言时才落到 zh-CN。
/// </summary>
internal static class NativeTextCatalog
{
    private static readonly IReadOnlyDictionary<string, NativeText> ByLanguage =
        new Dictionary<string, NativeText>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = ZhCn.Text,
            ["zh-TW"] = ZhTw.Text,
            ["en-US"] = EnUs.Text,
            ["ja-JP"] = JaJp.Text,
            ["ko-KR"] = KoKr.Text,
            ["fr-FR"] = FrFr.Text,
            ["de-DE"] = DeDe.Text,
            ["es-ES"] = EsEs.Text,
            ["es-MX"] = EsMx.Text,
            ["pt-BR"] = PtBr.Text,
            ["pt-PT"] = PtPt.Text,
            ["ru-RU"] = RuRu.Text,
            ["uk-UA"] = UkUa.Text,
            ["pl-PL"] = PlPl.Text,
            ["tr-TR"] = TrTr.Text,
            ["it-IT"] = ItIt.Text,
            ["nl-NL"] = NlNl.Text,
            ["sv-SE"] = SvSe.Text,
            ["fi-FI"] = FiFi.Text,
            ["da-DK"] = DaDk.Text,
            ["nb-NO"] = NbNo.Text,
            ["cs-CZ"] = CsCz.Text,
            ["hu-HU"] = HuHu.Text,
            ["ro-RO"] = RoRo.Text,
            ["el-GR"] = ElGr.Text,
            ["he-IL"] = HeIl.Text,
            ["ar-SA"] = ArSa.Text,
            ["hi-IN"] = HiIn.Text,
            ["id-ID"] = IdId.Text,
            ["vi-VN"] = ViVn.Text,
            ["th-TH"] = ThTh.Text
        };

    public static IEnumerable<string> Languages => ByLanguage.Keys;

    /// <summary>取某个具体语言 id 的完整文案包。</summary>
    public static NativeText For(string language) =>
        ByLanguage.TryGetValue(language, out var text) ? text : ByLanguage[NativeLanguage.Fallback];
}
