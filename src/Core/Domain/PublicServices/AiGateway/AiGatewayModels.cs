namespace ResourceManager.App.Domain.PublicServices.AiGateway;

public static class AiGatewayCompatibilityProfiles
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    public static bool IsSupported(string? value) =>
        value is not null
        && (value.Equals(OpenAi, StringComparison.OrdinalIgnoreCase)
            || value.Equals(Anthropic, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

public sealed record AiGatewayCredentialCreateRequest(
    string? DisplayName,
    string CompatibilityProfile);

public sealed record AiGatewayCredentialSummary(
    string Id,
    string DisplayName,
    string CompatibilityProfile,
    DateTimeOffset CreatedAt);

public sealed record AiGatewayCredentialCreated(
    AiGatewayCredentialSummary Credential,
    string ApiKey);

public sealed record AiGatewayCredentialView(
    string Id,
    string DisplayName,
    string CompatibilityProfile,
    DateTimeOffset CreatedAt,
    string BaseUrl,
    string AuthenticationHeader);

public sealed record AiGatewayCredentialCreatedView(
    AiGatewayCredentialView Credential,
    string ApiKey);
