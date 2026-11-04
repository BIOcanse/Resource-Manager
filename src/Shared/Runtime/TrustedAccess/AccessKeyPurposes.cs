namespace ResourceManager.Shared.Runtime.TrustedAccess;

public static class AccessKeyPurposes
{
    public const string Control = "control";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    public static bool IsSupported(string? value) =>
        value is not null
        && (value.Equals(Control, StringComparison.OrdinalIgnoreCase)
            || value.Equals(OpenAi, StringComparison.OrdinalIgnoreCase)
            || value.Equals(Anthropic, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
