namespace ResourceManager.NativeUi.Localization;

/// <summary>
/// NativeUi 界面语言的唯一运行时所有者。
///
/// 语言选择本身归前端设置所有（<c>appearance.language</c>，其中包含 "system" 这个正常取值）。
/// NativeUi 在前端可用之前从持久化设置读取该选择，之后前端每次切换语言都会通过
/// <c>shell.language</c> 消息把解析后的具体语言 id 送过来，这里原子替换当前文案并通知界面重绘。
/// 消费端只读 <see cref="Current"/>，永远拿到当前语言的确定字符串。
/// </summary>
internal static class NativeUiText
{
    private static string language = NativeLanguage.Fallback;
    private static NativeText current = NativeTextCatalog.For(NativeLanguage.Fallback);

    /// <summary>当前语言的完整文案包。</summary>
    public static NativeText Current => Volatile.Read(ref current!);

    /// <summary>当前生效的具体语言 id。</summary>
    public static string Language => Volatile.Read(ref language!);

    /// <summary>当前文案包被替换为另一种语言时触发，供界面重新取文案。</summary>
    public static event EventHandler? Changed;

    /// <summary>启动时的语言来源：持久化设置里的选择，按系统语言解析 "system"。</summary>
    public static void ApplyPersisted() => Apply(PersistedLanguageReader.Read());

    /// <summary>把语言选择（"system"、具体语言 id 或未知值）解析并应用为当前文案。</summary>
    public static void Apply(string? languageSelection)
    {
        var resolved = NativeLanguage.Resolve(languageSelection);
        if (string.Equals(resolved, Language, StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(ref current, NativeTextCatalog.For(resolved));
        Volatile.Write(ref language, resolved);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
