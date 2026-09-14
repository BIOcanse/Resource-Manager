using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Domain.PublicServices.AiGateway;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapAiGatewayCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai-gateway/credentials", async (
            HttpContext context,
            IAiGatewayCredentialService credentialService,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var credentials = await credentialService.ListAsync(cancellationToken);
            return Results.Ok(credentials.Select(item => ToView(context.Request, item)).ToArray());
        }).AllowAnonymous();

        app.MapPost("/api/ai-gateway/credentials", async (
            HttpContext context,
            AiGatewayCredentialCreateRequest request,
            IAiGatewayCredentialService credentialService,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var created = await credentialService.CreateAsync(request, cancellationToken);
                return Results.Ok(new AiGatewayCredentialCreatedView(
                    ToView(context.Request, created.Credential),
                    created.ApiKey));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = "invalid-ai-gateway-credential",
                    message = exception.Message
                });
            }
        });

        app.MapDelete("/api/ai-gateway/credentials/{credentialId}", async (
            string credentialId,
            IAiGatewayCredentialService credentialService,
            CancellationToken cancellationToken) =>
            await credentialService.RevokeAsync(credentialId, cancellationToken)
                ? Results.Ok(new { revoked = true })
                : Results.NotFound(new { error = "ai-gateway-credential-not-found" }));

        return app;
    }

    private static AiGatewayCredentialView ToView(
        HttpRequest request,
        AiGatewayCredentialSummary credential)
    {
        var origin = $"{request.Scheme}://{request.Host}";
        return new AiGatewayCredentialView(
            credential.Id,
            credential.DisplayName,
            credential.CompatibilityProfile,
            credential.CreatedAt,
            $"{origin}/api/public/v1/ai/compat/{credential.CompatibilityProfile}/v1",
            credential.CompatibilityProfile == AiGatewayCompatibilityProfiles.OpenAi
                ? "Authorization: Bearer"
                : "x-api-key");
    }
}
