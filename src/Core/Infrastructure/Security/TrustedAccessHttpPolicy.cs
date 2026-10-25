using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;
using Microsoft.AspNetCore.Routing;
using System.Collections.Frozen;

namespace ResourceManager.App.Infrastructure.Security;

internal enum TrustedAccessHttpAuthorizationMode
{
    Anonymous,
    Protected
}

internal sealed record TrustedAccessHttpRequirement(
    TrustedAccessHttpAuthorizationMode Mode,
    IReadOnlyList<string> RequiredScopes,
    bool AllowKeylessLocalWindowsCaller,
    bool RequireKeyless = false)
{
    public static TrustedAccessHttpRequirement Anonymous { get; } = new(
        TrustedAccessHttpAuthorizationMode.Anonymous,
        [],
        false);
}

internal sealed record TrustedAccessAuthorizationContext(
    AccessKeySummary? Key,
    LoopbackApiProcess? WindowsCaller,
    bool IsAdministrator,
    bool IsKeylessLocalCaller)
{
    public IReadOnlyList<string> EffectiveScopes => Key?.Scopes
        ?? (IsKeylessLocalCaller
            ? TrustedAccessScopeCatalog.LocalReadScopes
            : []);
}

internal sealed record TrustedAccessAuthorizationDecision(
    bool Allowed,
    int StatusCode,
    string ErrorCode,
    TrustedAccessAuthorizationContext? Context,
    string? AccessKeyId = null)
{
    public static TrustedAccessAuthorizationDecision Allow(
        TrustedAccessAuthorizationContext context)
        => new(true, StatusCodes.Status200OK, string.Empty, context, context.Key?.Id);

    public static TrustedAccessAuthorizationDecision Reject(
        int statusCode,
        string errorCode,
        string? accessKeyId = null)
        => new(false, statusCode, errorCode, null, accessKeyId);
}

internal static class TrustedAccessScopeCatalog
{
    public static IReadOnlyList<string> LocalReadScopes { get; } =
    [
        AccessKeyScopes.Read,
        AccessKeyScopes.MetricsRead,
        AccessKeyScopes.ProcessesRead
    ];

    public static IReadOnlyList<string> All { get; } =
    [
        AccessKeyScopes.Read,
        AccessKeyScopes.MetricsRead,
        AccessKeyScopes.ProcessesRead,
        AccessKeyScopes.Session,
        AccessKeyScopes.AdaptersRegisterSelf,
        AccessKeyScopes.DesktopPublish,
        AccessKeyScopes.GpuResolve,
        AccessKeyScopes.GpuReport,
        AccessKeyScopes.KeysManage,
        AccessKeyScopes.MachineWrite,
        AccessKeyScopes.InjectionConfigure
    ];

    public static IReadOnlyList<string> DefaultScopes { get; } = All
        .Where(static scope => scope is not AccessKeyScopes.MachineWrite
            and not AccessKeyScopes.InjectionConfigure)
        .ToArray();
}

internal static class TrustedAccessHttpPolicy
{
    private static readonly FrozenDictionary<(string Scope, bool AllowKeyless), TrustedAccessHttpRequirement>
        SingleScopeRequirements = TrustedAccessScopeCatalog.All
            .SelectMany(static scope => new[] { false, true }.Select(allowKeyless => new
            {
                Key = (scope, allowKeyless),
                Value = new TrustedAccessHttpRequirement(TrustedAccessHttpAuthorizationMode.Protected,
                    Array.AsReadOnly(new[] { scope }), allowKeyless)
            }))
            .ToFrozenDictionary(static item => item.Key, static item => item.Value);

    private static readonly TrustedAccessHttpRequirement InjectionConfigurationRequirement = new(
        TrustedAccessHttpAuthorizationMode.Protected,
        Array.AsReadOnly(new[] { AccessKeyScopes.MachineWrite, AccessKeyScopes.InjectionConfigure }), false);

    private static readonly TrustedAccessHttpRequirement KeylessRequestRequirement = new(
        TrustedAccessHttpAuthorizationMode.Protected, [], true, RequireKeyless: true);

    private static readonly TrustedAccessHttpRequirement SubscriptionRequirement = new(
        TrustedAccessHttpAuthorizationMode.Protected, [], false);

    public static bool IsTrustedAccessApiRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.StartsWith("/api/access-keys", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/access-requests", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/ai-gateway/credentials", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAiCompatibilityRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.StartsWith("/api/public/v1/ai/compat/openai/v1/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/public/v1/ai/compat/anthropic/v1/", StringComparison.OrdinalIgnoreCase);
    }

    public static TrustedAccessHttpRequirement GetRequirement(HttpRequest request)
    {
        var path = GetPolicyPath(request);
        var method = GetPolicyMethod(request);

        if (IsAnonymousCatalogOrHealth(path, method))
            return TrustedAccessHttpRequirement.Anonymous;

        if (IsAccessRequestWaitOrCreate(path, method))
        {
            return KeylessRequestRequirement;
        }

        if (path.StartsWith("/api/access-keys", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/ai-gateway/credentials", StringComparison.OrdinalIgnoreCase)
            || IsAccessRequestManagement(path, method))
        {
            return Protected(AccessKeyScopes.KeysManage);
        }

        if (path.Equals("/api/subscriptions/stream", StringComparison.OrdinalIgnoreCase))
        {
            return SubscriptionRequirement;
        }

        if (path.Equals("/api/runtime/interactive-desktop", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/runtime/interactive-desktop/publisher/select", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(AccessKeyScopes.DesktopPublish);
        }

        if (path.StartsWith("/api/device-topology/nvidia-drs/fields/", StringComparison.OrdinalIgnoreCase)
            && method == "POST")
        {
            return Protected(AccessKeyScopes.Read, allowKeylessLocal: true);
        }

        if (path.StartsWith("/api/disk-usage/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/components/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/verify", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/public/v1/sqlite/databases/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/query", StringComparison.OrdinalIgnoreCase)
                && method == "POST")
        {
            return Protected(AccessKeyScopes.Read, allowKeylessLocal: true);
        }

        if (path.Equals("/api/adapters/resource-manager/scheduling", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/adapters/resource-manager/scheduling/subscribe", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(AccessKeyScopes.AdaptersRegisterSelf);
        }

        if (path.StartsWith("/api/operations/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(AccessKeyScopes.Session);
        }

        if (path.StartsWith("/api/targeted-recordings", StringComparison.OrdinalIgnoreCase))
        {
            return method is "GET" or "HEAD"
                ? Protected(AccessKeyScopes.ProcessesRead, allowKeylessLocal: true)
                : method is "POST" or "DELETE"
                    ? Protected(AccessKeyScopes.Session)
                    : Protected(AccessKeyScopes.MachineWrite);
        }

        if (path.Equals("/api/optimization/reports/refresh", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/cpu/residency", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(AccessKeyScopes.ProcessesRead, allowKeylessLocal: true);
        }

        if (path.Equals("/api/gpu-placement/process-history/observe", StringComparison.OrdinalIgnoreCase))
            return Protected(AccessKeyScopes.GpuReport);

        if (path.Equals("/api/gpu-placement/process-policy", StringComparison.OrdinalIgnoreCase)
            && method == "PUT")
        {
            return InjectionConfigurationRequirement;
        }

        if (path.StartsWith("/api/metrics/", StringComparison.OrdinalIgnoreCase))
            return method is "GET" or "HEAD"
                ? Protected(AccessKeyScopes.MetricsRead, allowKeylessLocal: true)
                : Protected(AccessKeyScopes.MachineWrite);

        if (path.StartsWith("/api/resource-monitor/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/resource-breakdown/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/resource-table/", StringComparison.OrdinalIgnoreCase))
        {
            return method is "GET" or "HEAD"
                ? Protected(AccessKeyScopes.ProcessesRead, allowKeylessLocal: true)
                : Protected(AccessKeyScopes.MachineWrite);
        }

        if (path.StartsWith("/api/adapters/register", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/controlled/register", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(AccessKeyScopes.AdaptersRegisterSelf);
        }

        if (path.Equals("/api/operations/subscribe", StringComparison.OrdinalIgnoreCase))
            return Protected(AccessKeyScopes.Session);

        if (path.StartsWith("/api/performance-overlay/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("/api/performance-overlay/software/", StringComparison.OrdinalIgnoreCase)
                && method == "PUT")
            {
                return InjectionConfigurationRequirement;
            }

            return method is "GET" or "HEAD"
                ? Protected(AccessKeyScopes.MetricsRead, allowKeylessLocal: true)
                : Protected(AccessKeyScopes.MachineWrite);
        }

        if (path.StartsWith("/api/runtime/", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || path.Contains("adapter", StringComparison.OrdinalIgnoreCase)))
        {
            if (path.Contains("resolve", StringComparison.OrdinalIgnoreCase))
                return Protected(AccessKeyScopes.GpuResolve);
            if (path.Contains("report", StringComparison.OrdinalIgnoreCase))
                return Protected(AccessKeyScopes.GpuReport);
        }

        if (path.StartsWith("/api/gpu-placement/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/resolve", StringComparison.OrdinalIgnoreCase))
                return Protected(AccessKeyScopes.GpuResolve);
            if (path.Contains("/report", StringComparison.OrdinalIgnoreCase))
                return Protected(AccessKeyScopes.GpuReport);
        }

        if (path.StartsWith("/api/system/", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("open-path", StringComparison.OrdinalIgnoreCase)
                || path.Contains("open-properties", StringComparison.OrdinalIgnoreCase)
                || path.Contains("search-online", StringComparison.OrdinalIgnoreCase)))
        {
            return Protected(AccessKeyScopes.Session);
        }

        if (method is "GET" or "HEAD")
            return Protected(AccessKeyScopes.Read, allowKeylessLocal: true);

        var writeScope = path.StartsWith("/api/system/", StringComparison.OrdinalIgnoreCase)
            && path.Contains("session", StringComparison.OrdinalIgnoreCase)
                ? AccessKeyScopes.Session
                : AccessKeyScopes.MachineWrite;
        return Protected(writeScope);
    }

    public static IReadOnlyList<string> RequiredScopesForSubscription(string selector)
        => selector switch
        {
            "/api/metrics/subscribe" => [AccessKeyScopes.MetricsRead],
            "/api/metrics/fields/catalog/subscribe" => [],
            "/api/metrics/gpu-specialized/subscribe" => [AccessKeyScopes.MetricsRead],
            "/api/resource-monitor/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/performance-overlay/subscribe" => [AccessKeyScopes.MetricsRead],
            "/api/adapters/resource-manager/scheduling/subscribe" => [AccessKeyScopes.AdaptersRegisterSelf],
            "/api/control/actual/subscribe" => [AccessKeyScopes.Read],
            "/api/local-system/status/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/state/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/nvidia-drs/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/acpi/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/topology/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/topology/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/residency/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/optimization/smart/state/subscribe" => [AccessKeyScopes.Read],
            "/api/optimization/smart/status/subscribe" => [AccessKeyScopes.Read],
            "/api/operations/subscribe" => [AccessKeyScopes.Session],
            "/api/management/inventory-revisions/subscribe" => [AccessKeyScopes.Read],
            "/api/access-requests/subscribe" => [AccessKeyScopes.KeysManage],
            _ => throw new ArgumentException("The subscription selector has no Trusted Access policy.", nameof(selector))
        };

    public static bool AllowsKeylessSubscription(string selector)
        => RequiredScopesForSubscription(selector).All(TrustedAccessScopeCatalog.LocalReadScopes.Contains);

    public static bool IsStreamingRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/subscribe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase);
    }

    public static async ValueTask<TrustedAccessAuthorizationDecision> AuthorizeAsync(
        HttpContext context,
        ITrustedAccessKeyService keyService,
        ILoopbackApiAdministratorReader administratorReader,
        IReadOnlyList<string> requiredScopes,
        bool allowKeylessLocalWindowsCaller,
        bool requireKeyless,
        CancellationToken cancellationToken)
    {
        var headerValues = context.Request.Headers[TrustedAccessHeaders.KeyHeader];
        var keyHeaderPresent = context.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader);
        if (requireKeyless && keyHeaderPresent)
            return TrustedAccessAuthorizationDecision.Reject(StatusCodes.Status401Unauthorized, "keyless-request-required");

        if (keyHeaderPresent)
        {
            if (headerValues.Count != 1 || string.IsNullOrWhiteSpace(headerValues[0]))
                return TrustedAccessAuthorizationDecision.Reject(StatusCodes.Status401Unauthorized, "access-key-invalid");

            AccessKeySummary? key;
            try
            {
                key = await keyService.AuthenticateAsync(
                    AccessKeyPurposes.Control,
                    headerValues[0]!,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return TrustedAccessAuthorizationDecision.Reject(
                    StatusCodes.Status503ServiceUnavailable,
                    "access-service-unavailable");
            }
            if (key is null)
                return TrustedAccessAuthorizationDecision.Reject(StatusCodes.Status401Unauthorized, "access-key-invalid");

            if (!ContainsAll(key.Scopes, requiredScopes))
                return TrustedAccessAuthorizationDecision.Reject(
                    StatusCodes.Status403Forbidden,
                    "access-scope-required",
                    key.Id);

            var requiresElevation = false;
            for (var index = 0; index < requiredScopes.Count; index++)
            {
                if (requiredScopes[index] is AccessKeyScopes.MachineWrite or AccessKeyScopes.InjectionConfigure)
                {
                    requiresElevation = true;
                    break;
                }
            }
            var administrator = requiresElevation && administratorReader.IsAdministrator(context);
            if (requiresElevation)
            {
                if (!administrator)
                    return TrustedAccessAuthorizationDecision.Reject(
                        StatusCodes.Status403Forbidden,
                        "elevated-administrator-required",
                        key.Id);
            }

            var authorization = new TrustedAccessAuthorizationContext(key, null, administrator, false);
            context.Items[typeof(TrustedAccessAuthorizationContext)] = authorization;
            return TrustedAccessAuthorizationDecision.Allow(authorization);
        }

        var isAdministrator = administratorReader.IsAdministrator(context);
        if (isAdministrator)
        {
            var authorization = new TrustedAccessAuthorizationContext(null, null, true, false);
            context.Items[typeof(TrustedAccessAuthorizationContext)] = authorization;
            return TrustedAccessAuthorizationDecision.Allow(authorization);
        }

        var connectedCaller = TryReadConnectedCaller(context, out var connectedPeer) ? connectedPeer : null;
        if (allowKeylessLocalWindowsCaller && connectedCaller is not null)
        {
            var authorization = new TrustedAccessAuthorizationContext(null, connectedCaller, false, true);
            context.Items[typeof(TrustedAccessAuthorizationContext)] = authorization;
            return TrustedAccessAuthorizationDecision.Allow(authorization);
        }

        return TrustedAccessAuthorizationDecision.Reject(
            connectedCaller is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden,
            connectedCaller is null ? "local-windows-caller-required" : "access-key-required");
    }

    private static TrustedAccessHttpRequirement Protected(
        string scope,
        bool allowKeylessLocal = false)
        => SingleScopeRequirements[(scope, allowKeylessLocal)];

    private static bool IsAnonymousCatalogOrHealth(string path, string method)
    {
        if (method is not ("GET" or "HEAD")) return false;
        return path.Equals("/api/runtime/capabilities", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/public/v1", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/public/v1/capabilities", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/public/v1/capability", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/metrics/catalog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/metrics/fields/capabilities", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/metrics/fields/catalog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/resource-breakdown/fields/catalog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccessRequestWaitOrCreate(string path, string method)
        => method == "POST"
            && (path.Equals("/api/access-requests", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/access-requests/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("/wait", StringComparison.OrdinalIgnoreCase));

    private static bool IsAccessRequestManagement(string path, string method)
        => method == "POST"
            && path.StartsWith("/api/access-requests/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/approve", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/deny", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAll(IReadOnlyList<string> granted, IReadOnlyList<string> required)
    {
        for (var index = 0; index < required.Count; index++)
        {
            var found = false;
            for (var candidateIndex = 0; candidateIndex < granted.Count; candidateIndex++)
            {
                if (!string.Equals(granted[candidateIndex], required[index], StringComparison.Ordinal)) continue;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    private static string GetPolicyMethod(HttpRequest request)
    {
        // Routing matches HTTP methods without case sensitivity. Use its matched
        // metadata when available, and the same comparison for unmatched requests.
        var method = request.Method;
        var methods = request.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (methods is not null)
        {
            foreach (var candidate in methods)
            {
                if (!string.Equals(candidate, method, StringComparison.OrdinalIgnoreCase)) continue;
                method = candidate;
                break;
            }
        }
        if (HttpMethods.IsGet(method)) return "GET";
        if (HttpMethods.IsHead(method)) return "HEAD";
        if (HttpMethods.IsPost(method)) return "POST";
        if (HttpMethods.IsPut(method)) return "PUT";
        if (HttpMethods.IsDelete(method)) return "DELETE";
        return method;
    }

    private static string GetPolicyPath(HttpRequest request)
    {
        if (request.HttpContext.GetEndpoint() is RouteEndpoint endpoint
            && !string.IsNullOrWhiteSpace(endpoint.RoutePattern.RawText))
        {
            return endpoint.RoutePattern.RawText;
        }

        return request.Path.Value ?? string.Empty;
    }

    private static bool TryReadConnectedCaller(HttpContext context, out LoopbackApiProcess peer)
        => WindowsLoopbackApiAdministratorReader.TryReadConnectedCaller(context, out peer);
}
