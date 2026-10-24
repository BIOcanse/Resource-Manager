using System.Text.Json;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiGateway;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private const long MaximumAiGatewayRequestBytes = 32L * 1024 * 1024;
    private const int AiGatewayMemoryBufferBytes = 4 * 1024 * 1024;

    private static IEndpointRouteBuilder MapPublicAiModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/v1/ai/status", async (
            ILocalAiModelService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetStatusAsync(cancellationToken))).AllowAnonymous();

        app.MapGet("/api/public/v1/ai/models", async (
            ILocalAiModelService service,
            CancellationToken cancellationToken) =>
            await RunAiAsync(() => service.ListModelsAsync(cancellationToken))).AllowAnonymous();

        app.MapPost("/api/public/v1/ai/models/load", async (
            AiModelLoadRequest request,
            ILocalAiModelService service,
            CancellationToken cancellationToken) =>
            await RunAiAsync(() => service.LoadAsync(request, cancellationToken)));

        app.MapPost("/api/public/v1/ai/models/unload", async (
            AiModelUnloadRequest request,
            ILocalAiModelService service,
            CancellationToken cancellationToken) =>
            await RunAiAsync(() => service.UnloadAsync(request, cancellationToken)));

        MapAiGatewayRoute(
            app,
            HttpMethods.Get,
            "/api/public/v1/ai/compat/openai/v1/models",
            "/v1/models",
            requiresLocalModel: false);
        MapAiGatewayRoute(
            app,
            HttpMethods.Post,
            "/api/public/v1/ai/compat/openai/v1/chat/completions",
            "/v1/chat/completions",
            requiresLocalModel: true);
        MapAiGatewayRoute(
            app,
            HttpMethods.Post,
            "/api/public/v1/ai/compat/openai/v1/embeddings",
            "/v1/embeddings",
            requiresLocalModel: true);
        MapAiGatewayRoute(
            app,
            HttpMethods.Post,
            "/api/public/v1/ai/compat/openai/v1/responses",
            "/v1/responses",
            requiresLocalModel: true);
        MapAiGatewayRoute(
            app,
            HttpMethods.Post,
            "/api/public/v1/ai/compat/anthropic/v1/messages",
            "/v1/messages",
            requiresLocalModel: true);

        return app;
    }

    private static void MapAiGatewayRoute(
        IEndpointRouteBuilder app,
        string method,
        string route,
        string providerPath,
        bool requiresLocalModel)
    {
        app.MapMethods(route, [method], async (
            HttpContext context,
            IAiGatewayCredentialService credentials,
            IAiGatewayLocalModelPolicy localModelPolicy,
            ILocalAiModelService modelService,
            CancellationToken cancellationToken) =>
        {
            var profile = route.Contains("/anthropic/", StringComparison.Ordinal)
                ? AiGatewayCompatibilityProfiles.Anthropic : AiGatewayCompatibilityProfiles.OpenAi;
            var key = ReadAiGatewayKey(context.Request, profile);
            if (!await credentials.ValidateAsync(profile, key, cancellationToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new EndpointErrorPayload("ai-gateway-key-required"), cancellationToken);
                return;
            }
            await ForwardAiGatewayAsync(
                context,
                providerPath,
                requiresLocalModel,
                localModelPolicy,
                modelService,
                cancellationToken);
        }).AllowAnonymous();
    }

    internal static string? ReadAiGatewayKey(HttpRequest request, string profile)
    {
        var name = profile == AiGatewayCompatibilityProfiles.Anthropic ? "x-api-key" : "Authorization";
        if (!request.Headers.TryGetValue(name, out var values) || values.Count != 1) return null;
        var value = values[0]?.Trim();
        if (profile == AiGatewayCompatibilityProfiles.Anthropic) return value;
        return value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? value[7..].Trim() : null;
    }

    private static async Task ForwardAiGatewayAsync(
        HttpContext context,
        string providerPath,
        bool requiresLocalModel,
        IAiGatewayLocalModelPolicy localModelPolicy,
        ILocalAiModelService modelService,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (requiresLocalModel)
            {
                var model = await ReadRequestModelAsync(context.Request, cancellationToken);
                if (model is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(
                        new EndpointErrorMessagePayload("local-model-required", "The request must select a local model."),
                        cancellationToken);
                    return;
                }

                if (!await localModelPolicy.IsAllowedAsync(model, cancellationToken))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(
                        new EndpointErrorMessagePayload("unknown-local-model", "The selected model is not in the local LM Studio catalog."),
                        cancellationToken);
                    return;
                }
            }

            await modelService.ForwardAsync(context, providerPath, cancellationToken);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new EndpointErrorMessagePayload("invalid-ai-gateway-request", exception.Message),
                cancellationToken);
        }
        catch (JsonException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new EndpointErrorMessagePayload("invalid-json", exception.Message),
                cancellationToken);
        }
        catch (AiModelRuntimeUnavailableException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new EndpointErrorMessagePayload("ai-model-runtime-unavailable", exception.Message),
                cancellationToken);
        }
        catch (HttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new EndpointErrorMessagePayload("ai-model-provider-error", exception.Message),
                cancellationToken);
        }
    }

    private static async Task<string?> ReadRequestModelAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumAiGatewayRequestBytes)
        {
            throw new BadHttpRequestException(
                "AI gateway request body is too large.",
                StatusCodes.Status413PayloadTooLarge);
        }

        request.EnableBuffering(AiGatewayMemoryBufferBytes, MaximumAiGatewayRequestBytes);
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                ? model.GetString()?.Trim()
                : null;
        }
        catch (IOException exception)
        {
            throw new BadHttpRequestException(
                "AI gateway request body is too large.",
                StatusCodes.Status413PayloadTooLarge,
                exception);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static async Task<IResult> RunAiAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new EndpointErrorMessagePayload("invalid-ai-model-request", exception.Message));
        }
        catch (AiModelRuntimeUnavailableException exception)
        {
            return Results.Json(
                new EndpointErrorMessagePayload("ai-model-runtime-unavailable", exception.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException exception)
        {
            return Results.Json(
                new EndpointErrorMessagePayload("ai-model-provider-error", exception.Message),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
