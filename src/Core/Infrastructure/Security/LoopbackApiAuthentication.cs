using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace ResourceManager.App.Infrastructure.Security;

public static class LoopbackApiAuthenticationPolicy
{
    public static bool IsLoopbackConnection(HttpContext context)
    {
        var remote = NormalizeLoopbackAddress(context.Connection.RemoteIpAddress);
        var local = NormalizeLoopbackAddress(context.Connection.LocalIpAddress);
        return remote is not null && local is not null
            && System.Net.IPAddress.IsLoopback(remote)
            && System.Net.IPAddress.IsLoopback(local);
    }

    public static bool IsAllowedBrowserRequest(HttpRequest request, int localPort)
    {
        var host = request.Host.Host.Trim('[', ']');
        var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        if (!localHost || port != localPort) return false;
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            && origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
            && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.UserInfo.Length == 0;
    }

    private static System.Net.IPAddress? NormalizeLoopbackAddress(System.Net.IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
}

public sealed class LoopbackApiAuthenticationMiddleware(
    RequestDelegate next,
    ILoopbackApiAdministratorReader administratorReader,
    ILogger<LoopbackApiAuthenticationMiddleware>? logger = null)
{
    public async Task InvokeAsync(HttpContext context, ITrustedAccessKeyService keyService)
    {
        if (TrustedAccessHttpPolicy.IsTrustedAccessApiRequest(context.Request))
            context.Response.Headers["Cache-Control"] = "no-store";

        if (RequiresTrustedAccessBodyLimit(context.Request))
        {
            if (context.Request.ContentLength is { } contentLength
                && contentLength > TrustedAccessApiContract.AccessRequestMaximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(
                    new LoopbackApiErrorPayload("request-body-too-large"),
                    context.RequestAborted);
                return;
            }

            var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is null || bodySizeFeature.IsReadOnly)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new LoopbackApiErrorPayload("request-body-limit-unavailable"),
                    context.RequestAborted);
                return;
            }

            bodySizeFeature.MaxRequestBodySize = TrustedAccessApiContract.AccessRequestMaximumBodyBytes;
        }

        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (!LoopbackApiAuthenticationPolicy.IsLoopbackConnection(context)
            || !LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, context.Connection.LocalPort))
        {
            LogAuthorizationByKey(context, null, "denied:loopback-origin-required");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new LoopbackApiErrorPayload("loopback-origin-required"),
                context.RequestAborted);
            return;
        }

        // The subscription endpoint authorizes every selector in the multiplexed body.
        // It cannot be authorized once at the HTTP path because one body may request several scopes.
        if (context.Request.Path.Equals("/api/subscriptions/stream", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals("/api/hooks/wait", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // These routes authenticate their own OpenAI/Anthropic credential purpose.
        // A control key never substitutes for that credential; an explicitly supplied
        // control key is still validated and rejected if it is invalid.
        if (TrustedAccessHttpPolicy.IsAiCompatibilityRequest(context.Request))
        {
            if (context.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader))
            {
                var explicitCredentialDecision = await TrustedAccessHttpPolicy.AuthorizeAsync(
                    context,
                    keyService,
                    administratorReader,
                    [],
                    allowKeylessLocalWindowsCaller: false,
                    requireKeyless: false,
                    context.RequestAborted);
                if (!explicitCredentialDecision.Allowed)
                {
                    LogAuthorization(context, explicitCredentialDecision, $"denied:{explicitCredentialDecision.ErrorCode}");
                    context.Response.StatusCode = explicitCredentialDecision.StatusCode;
                    await context.Response.WriteAsJsonAsync(
                        new LoopbackApiErrorPayload(explicitCredentialDecision.ErrorCode),
                        context.RequestAborted);
                    return;
                }
                LogAuthorization(context, explicitCredentialDecision, "allowed");
            }
            else
            {
                LogAuthorizationByKey(context, null, "delegated-purpose-authentication");
            }
            await InvokePublicServicePolicyOrNextAsync(context);
            return;
        }

        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        if (requirement.Mode == TrustedAccessHttpAuthorizationMode.MissingPolicy)
        {
            LogAuthorizationByKey(context, null, "denied:access-policy-missing");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new LoopbackApiErrorPayload("access-policy-missing"), context.RequestAborted);
            return;
        }
        if (requirement.Mode == TrustedAccessHttpAuthorizationMode.Anonymous)
        {
            if (context.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader))
            {
                var explicitCredentialDecision = await TrustedAccessHttpPolicy.AuthorizeAsync(
                    context,
                    keyService,
                    administratorReader,
                    [],
                    allowKeylessLocalWindowsCaller: false,
                    requireKeyless: false,
                    context.RequestAborted);
                if (!explicitCredentialDecision.Allowed)
                {
                    LogAuthorization(context, explicitCredentialDecision, $"denied:{explicitCredentialDecision.ErrorCode}");
                    context.Response.StatusCode = explicitCredentialDecision.StatusCode;
                    await context.Response.WriteAsJsonAsync(
                        new LoopbackApiErrorPayload(explicitCredentialDecision.ErrorCode),
                        context.RequestAborted);
                    return;
                }
                LogAuthorization(context, explicitCredentialDecision, "allowed");
            }
            else
            {
                LogAuthorizationByKey(context, null, "allowed:anonymous-catalog-or-health");
            }
            await InvokePublicServicePolicyOrNextAsync(context);
            return;
        }

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            requirement.RequiredScopes,
            requirement.AllowKeylessLocalWindowsCaller,
            requirement.RequireKeyless,
            context.RequestAborted);
        if (!decision.Allowed)
        {
            LogAuthorization(context, decision, $"denied:{decision.ErrorCode}");
            context.Response.StatusCode = decision.StatusCode;
            await (decision.MissingScopes.Count > 0
                ? context.Response.WriteAsJsonAsync(
                    new LoopbackApiScopeErrorPayload(decision.ErrorCode, decision.MissingScopes),
                    context.RequestAborted)
                : context.Response.WriteAsJsonAsync(
                    new LoopbackApiErrorPayload(decision.ErrorCode),
                    context.RequestAborted));
            return;
        }
        LogAuthorization(context, decision, "allowed");

        if (decision.Context?.Key is not null
            && TrustedAccessHttpPolicy.IsStreamingRequest(context.Request))
        {
            await InvokeAuthorizedSafelyAsync(context, keyService, decision);
            return;
        }

        await InvokeAuthorizedSafelyAsync(context, keyService, decision);
    }

    private async Task InvokeAuthorizedSafelyAsync(
        HttpContext context,
        ITrustedAccessKeyService keyService,
        TrustedAccessAuthorizationDecision decision)
    {
        var requestAborted = context.RequestAborted;
        CancellationTokenSource? revocation = null;
        try
        {
            if (decision.Context?.Key is { } key
                && TrustedAccessHttpPolicy.IsStreamingRequest(context.Request))
            {
                revocation = CancellationTokenSource.CreateLinkedTokenSource(
                    requestAborted,
                    keyService.GetRevocationToken(key.Id));
                context.RequestAborted = revocation.Token;
            }

            await InvokePublicServicePolicyOrNextAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (TrustedAccessHttpPolicy.IsTrustedAccessApiRequest(context.Request))
        {
            LogAuthorizationByKey(context, decision.AccessKeyId, "unavailable");
            if (logger is not null)
            {
                logger.LogWarning(
                    "Trusted access operation {Operation} failed ({ErrorType}).",
                    GetSafeOperation(context),
                    error.GetType().Name);
            }
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new LoopbackApiErrorPayload("access-service-unavailable"),
                    requestAborted);
            }
            else
            {
                context.Abort();
            }
        }
        finally
        {
            context.RequestAborted = requestAborted;
            revocation?.Dispose();
        }
    }

    private void LogAuthorization(
        HttpContext context,
        TrustedAccessAuthorizationDecision decision,
        string result)
        => LogAuthorizationByKey(context, decision.AccessKeyId, result);

    private void LogAuthorizationByKey(HttpContext context, string? accessKeyId, string result)
    {
        if (logger is null)
            return;
        var route = context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? "<unnamed-route>"
            : "<unmatched-api-route>";
        logger.LogInformation(
            "Trusted access key {AccessKeyId} operation {Operation} result {Result}",
            accessKeyId ?? "none",
            $"{context.Request.Method} {route}",
            result);
    }

    private static string GetSafeOperation(HttpContext context)
    {
        var route = context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? "<unnamed-route>"
            : "<unmatched-api-route>";
        return $"{context.Request.Method} {route}";
    }

    private async Task InvokePublicServicePolicyOrNextAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/public/v1"))
        {
            await next(context);
            return;
        }

        var policy = context.RequestServices.GetRequiredService<ILocalPublicServiceAccessPolicy>();
        var decision = await policy.EvaluateAsync(context, context.RequestAborted);
        if (!decision.Allowed)
        {
            context.Response.StatusCode = decision.StatusCode;
            if (decision.StatusCode == StatusCodes.Status404NotFound
                && decision.Reason == "service-disabled")
            {
                return;
            }
            await context.Response.WriteAsJsonAsync(
                new LoopbackApiErrorPayload(decision.Reason),
                context.RequestAborted);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            await policy.CompleteAsync(decision.CompletionHandle, CancellationToken.None);
        }
    }

    private static bool RequiresTrustedAccessBodyLimit(HttpRequest request)
        => request.Path.StartsWithSegments(TrustedAccessApiContract.AccessKeysPath,
                StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments(TrustedAccessApiContract.AccessRequestsPath,
                StringComparison.OrdinalIgnoreCase);
}

internal sealed record LoopbackApiErrorPayload(string error);

// access-scope-required names the scopes the key lacks, as backend scope ids.
internal sealed record LoopbackApiScopeErrorPayload(string error, IReadOnlyList<string> missingScopes);
