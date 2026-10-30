using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;
using Microsoft.AspNetCore.Routing;
using System.Collections.Frozen;

namespace ResourceManager.App.Infrastructure.Security;

internal enum TrustedAccessHttpAuthorizationMode
{
    Anonymous,
    MissingPolicy,
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

    // A keyless elevated administrator is admitted to every route, so it holds every scope.
    public bool Holds(string scope)
        => IsAdministrator || EffectiveScopes.Contains(scope, StringComparer.Ordinal);
}

internal sealed record TrustedAccessAuthorizationDecision(
    bool Allowed,
    int StatusCode,
    string ErrorCode,
    TrustedAccessAuthorizationContext? Context,
    string? AccessKeyId = null)
{
    // The route's scopes that the key lacks; set only with access-scope-required.
    public IReadOnlyList<string> MissingScopes { get; init; } = [];

    public static TrustedAccessAuthorizationDecision Allow(
        TrustedAccessAuthorizationContext context)
        => new(true, StatusCodes.Status200OK, string.Empty, context, context.Key?.Id);

    public static TrustedAccessAuthorizationDecision Reject(
        int statusCode,
        string errorCode,
        string? accessKeyId = null)
        => new(false, statusCode, errorCode, null, accessKeyId);
}

// Views of the scope table in AccessKeyScopes; no scope list is declared here.
internal static class TrustedAccessScopeCatalog
{
    public static IReadOnlyList<string> LocalReadScopes => AccessKeyScopes.KeylessLocal;

    public static IReadOnlyList<string> All => AccessKeyScopes.All;

    public static IReadOnlyList<string> DefaultScopes { get; } = Array.AsReadOnly(AccessKeyScopes.Default);
}

internal static class TrustedAccessHttpPolicy
{
    private static readonly FrozenDictionary<string, TrustedAccessHttpRequirement> SingleScopeRequirements =
        TrustedAccessScopeCatalog.All.ToFrozenDictionary(
            static scope => scope,
            static scope => ScopeRequirement([scope]),
            StringComparer.Ordinal);

    private static readonly TrustedAccessHttpRequirement InjectionConfigurationRequirement =
        ScopeRequirement([AccessKeyScopes.MachineWrite, AccessKeyScopes.InjectionConfigure]);

    private static readonly TrustedAccessHttpRequirement KeylessRequestRequirement = new(
        TrustedAccessHttpAuthorizationMode.Protected, [], true, RequireKeyless: true);

    private static readonly TrustedAccessHttpRequirement MissingPolicyRequirement = new(
        TrustedAccessHttpAuthorizationMode.MissingPolicy, [], false);

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

    private static readonly FrozenDictionary<(string Method, string Path), TrustedAccessHttpRequirement> RouteRequirements = BuildRouteRequirements();

    private static FrozenDictionary<(string Method, string Path), TrustedAccessHttpRequirement> BuildRouteRequirements()
    {
        var routes = new Dictionary<(string Method, string Path), TrustedAccessHttpRequirement>(OperationComparer.Instance);
        // Every method/template has an explicit rule; new routes receive no inferred read/write fallback.
        // AI compatibility entries delegate credential-purpose checks to their endpoint.
        Add(Protected(AccessKeyScopes.KeysManage),
            "DELETE /api/access-keys/{id}",
            "DELETE /api/ai-gateway/credentials/{credentialId}",
            "GET /api/access-keys",
            "GET /api/ai-gateway/credentials",
            "POST /api/access-keys",
            "POST /api/access-keys/{id}/rotate",
            "POST /api/access-keys/default",
            "POST /api/access-requests/{requestId}/approve",
            "POST /api/access-requests/{requestId}/deny",
            "POST /api/ai-gateway/credentials");
        Add(Protected(AccessKeyScopes.MachineWrite),
            "POST /api/cpu/leases",
            "GET /api/cpu/leases/{leaseId}",
            "DELETE /api/cpu/leases/{leaseId}",
            "DELETE /api/adapters/{id}",
            "DELETE /api/controlled/{id:guid}",
            "DELETE /api/cpu/baseline-ratio",
            "DELETE /api/cpu/topology/performance-overrides",
            "DELETE /api/gpu/performance-overrides",
            "DELETE /api/optimization/protection/entries/{protectionId}",
            "PATCH /api/settings/app",
            "PATCH /api/memory-protection",
            "POST /api/memory-protection/reserve/rebuild",
            "POST /api/components/{id}/install",
            "POST /api/control/instances/refresh",
            "POST /api/control/redetect",
            "POST /api/control/state/{objectId}/retry",
            "POST /api/optimization/smart/mode",
            "POST /api/optimization/smart/run-once",
            "POST /api/optimization/smart/validation/process-effect-scope/close",
            "POST /api/optimization/smart/validation/process-effect-scope/open",
            "POST /api/public/v1/ai/models/load",
            "POST /api/public/v1/ai/models/unload",
            "POST /api/public/v1/sqlite/databases/{databaseId}/batch",
            "POST /api/public/v1/sqlite/databases/{databaseId}/checkpoint",
            "POST /api/public/v1/sqlite/databases/{databaseId}/execute",
            "POST /api/settings/app/reapply",
            "POST /api/software/uninstall",
            "POST /api/system/processes/dump",
            "POST /api/system/processes/terminate",
            "POST /api/updates/product/apply",
            "POST /api/updates/product/prepare",
            "PUT /api/control/access-level",
            "PUT /api/control/notice",
            "PUT /api/control/overclock-consent",
            "PUT /api/control/state",
            "PUT /api/control/state/{objectId}",
            "PUT /api/cpu/baseline-ratio",
            "PUT /api/cpu/topology/performance-overrides",
            "PUT /api/gpu-placement/software/{softwareId}/policy",
            "PUT /api/gpu/performance-overrides",
            "PUT /api/optimization/protection/software/{softwareId}",
            "PUT /api/optimization/protection/entries/{protectionId}/level",
            "PUT /api/public/v1/sqlite/databases/{databaseId}",
            "PUT /api/settings/app",
            "PUT /api/settings/dashboard");
        // Creating, selecting and deleting normal running points only records or picks the baseline; it changes
        // nothing on the machine, so the user does it from the bundled UI without elevation (user 10-28).
        // Dismissing an optimization report and removing a trust entry are session actions too (user 10-29).
        // Saving or deleting a control preset, forgetting a control instance, registering or deleting manual
        // software and confirming a portable root only record or drop an entry (user 10-29).
        Add(Protected(AccessKeyScopes.Session),
            "POST /api/optimization/reports/{id}/dismiss",
            "DELETE /api/optimization/trust/{id}",
            "PUT /api/control/presets/{objectId}/{name}",
            "DELETE /api/control/presets/{presetId}",
            "DELETE /api/control/instances/{instanceId}",
            "POST /api/software/manual",
            "DELETE /api/software/manual/{id}",
            "POST /api/software/portable/root",
            "POST /api/optimization/reports/history/clear",
            "DELETE /api/targeted-recordings/{id}",
            "POST /api/kernel-leaks/history/clear",
            "POST /api/targeted-recordings/history/clear",
            "GET /api/optimization/smart/records/history",
            "POST /api/optimization/smart/records/history/clear",
            "POST /api/memory-protection/normal-running-point",
            "PUT /api/memory-protection/normal-running-points/active",
            "DELETE /api/memory-protection/normal-running-points/{id}",
            "POST /api/memory-protection/records/history/clear",
            "POST /api/operations/{id}/cancel",
            "POST /api/system/open-path",
            "POST /api/system/open-properties",
            "POST /api/system/search-online",
            "POST /api/targeted-recordings",
            "POST /api/targeted-recordings/{id}/stop");
        Add(Protected(AccessKeyScopes.Read),
            "GET /api/adapters",
            "GET /api/browser-runtimes",
            "GET /api/components",
            "GET /api/components/{id}/versions",
            "GET /api/control/access-level",
            "GET /api/control/actual",
            "GET /api/control/instances",
            "GET /api/control/notice",
            "GET /api/control/objects",
            "GET /api/control/objects/{objectId}/curve",
            "GET /api/control/overclock-consent",
            "GET /api/control/presets",
            "GET /api/control/state",
            "GET /api/controlled",
            "GET /api/cpu/baseline-ratio",
            "GET /api/cpu/topology",
            "GET /api/cpu/topology/exclusive-bindings",
            "GET /api/cpu/topology/fields",
            "GET /api/cpu/residency/cores",
            "GET /api/debug/amd-smu",
            "GET /api/debug/business-blocks",
            "GET /api/debug/cpu-residency",
            "GET /api/debug/freedom-points",
            "GET /api/debug/gpu-control",
            "GET /api/debug/history/requirements",
            "GET /api/debug/logs",
            "GET /api/debug/native-memory",
            "GET /api/dependencies",
            "GET /api/device-topology/acpi/fields",
            "GET /api/device-topology/fields",
            "GET /api/device-topology/network-endpoints/fields",
            "GET /api/device-topology/state",
            "GET /api/disk-usage/node/{nodeId:int}",
            "GET /api/disk-usage/summary",
            "GET /api/disk-usage/tree",
            "GET /api/disk-usage/volumes",
            "GET /api/gpu-placement/software/{softwareId}/policy/default",
            "GET /api/gpu/performance-overrides",
            "GET /api/gpu/performance-scores",
            "GET /api/index/files/search",
            "GET /api/index/software/{softwareId}",
            "GET /api/local-system/status",
            "GET /api/notifications",
            "GET /api/operations",
            "GET /api/operations/{id}",
            "GET /api/optimization/protection",
            "GET /api/optimization/smart/status",
            "GET /api/public/v1/ai/models",
            "GET /api/public/v1/ai/status",
            "GET /api/public/v1/browser-runtimes",
            "GET /api/public/v1/index/files/search",
            "GET /api/public/v1/index/software/{softwareId}",
            "GET /api/public/v1/index/status",
            "GET /api/public/v1/resources",
            "GET /api/public/v1/resources/{publicResourceId}",
            "GET /api/public/v1/resources/transport",
            "GET /api/public/v1/sqlite/databases",
            "GET /api/public/v1/sqlite/databases/{databaseId}",
            "GET /api/runtime/identity",
            "GET /api/startup-modules/catalog",
            "GET /api/startup-modules/settings",
            "GET /api/startup-modules/software/{softwareId}",
            "GET /api/settings/app",
            "GET /api/settings/dashboard",
            "GET /api/settings/report-rules",
            "GET /api/software",
            "GET /api/software/manual",
            "GET /api/software/metadata/{softwareIdentityId}",
            "GET /api/system/host-manager/deployment",
            "GET /api/updates/product/status",
            "GET /api/updates/product/versions",
            "POST /api/components/{id}/verify",
            "POST /api/device-topology/nvidia-drs/fields/current",
            "POST /api/device-topology/nvidia-drs/fields/discover",
            "POST /api/device-topology/nvidia-drs/fields/read",
            "POST /api/disk-usage/scan",
            "POST /api/public/v1/sqlite/databases/{databaseId}/query");
        Add(Protected(AccessKeyScopes.AdaptersRegisterSelf),
            "POST /api/adapters/register",
            "POST /api/controlled/register");
        Add(Protected(AccessKeyScopes.ProcessesRead),
            "GET /api/memory-protection",
            "GET /api/memory-protection/normal-running-point",
            "GET /api/memory-protection/normal-running-points",
            "GET /api/cpu/residency",
            "GET /api/debug/frame-timing",
            "GET /api/debug/event-drops",
            "GET /api/debug/thread-cpu",
            "GET /api/debug/gpu-renderers",
            "GET /api/debug/scheduling-inputs",
            "GET /api/gpu-placement/software/{softwareId}",
            "GET /api/optimization/reports",
            "GET /api/optimization/smart/diagnostics/decision-snapshot/subscribe",
            "GET /api/optimization/smart/state",
            "GET /api/optimization/smart/validation/memory-cleanup-evidence",
            "GET /api/optimization/smart/validation/process-effect-scope",
            "GET /api/resource-breakdown/snapshot",
            "GET /api/resource-facts/snapshot",
            "GET /api/debug/kernel-leaks",
            "GET /api/resource-monitor/snapshot",
            "GET /api/targeted-recordings",
            "GET /api/targeted-recordings/{id}",
            "GET /api/targeted-recordings/{id}/export",
            "POST /api/optimization/reports/refresh");
        Add(TrustedAccessHttpRequirement.Anonymous,
            "GET /api/health",
            "GET /api/metrics/catalog",
            "GET /api/metrics/fields/capabilities",
            "GET /api/metrics/fields/catalog",
            "GET /api/public/v1",
            "GET /api/public/v1/ai/compat/openai/v1/models",
            "GET /api/public/v1/capabilities",
            "GET /api/public/v1/capability",
            "GET /api/resource-breakdown/fields/catalog",
            "GET /api/runtime/capabilities",
            "POST /api/public/v1/ai/compat/anthropic/v1/messages",
            "POST /api/public/v1/ai/compat/openai/v1/chat/completions",
            "POST /api/public/v1/ai/compat/openai/v1/embeddings",
            "POST /api/public/v1/ai/compat/openai/v1/responses");
        Add(Protected(AccessKeyScopes.MetricsRead),
            "GET /api/debug/metrics/capture",
            "GET /api/metrics/fields/snapshot",
            "GET /api/metrics/gpu-specialized",
            "GET /api/metrics/snapshot",
            "GET /api/performance-overlay/metrics/catalog");
        Add(KeylessRequestRequirement,
            "POST /api/access-requests",
            "POST /api/access-requests/{requestId}/wait");
        Add(Protected(AccessKeyScopes.GpuReport),
            "POST /api/gpu-placement/process-history/observe");
        Add(Protected(AccessKeyScopes.DesktopPublish),
            "POST /api/runtime/interactive-desktop",
            "POST /api/runtime/interactive-desktop/publisher/select");
        Add(SubscriptionRequirement,
            "POST /api/subscriptions/stream",
            "GET /api/hooks/wait");
        Add(TrustedAccessHttpRequirement.Anonymous, "GET /api/hooks/vocabulary");
        Add(InjectionConfigurationRequirement,
            "PUT /api/gpu-placement/process-policy",
            "PUT /api/startup-modules/software/{softwareId}");
        return routes.ToFrozenDictionary(OperationComparer.Instance);

        void Add(TrustedAccessHttpRequirement requirement, params string[] operations)
        {
            foreach (var operation in operations)
            {
                var separator = operation.IndexOf(' ');
                var method = operation[..separator];
                var path = operation[(separator + 1)..];
                routes.Add((method, path), requirement);
                if (method == "GET")
                    routes.Add(("HEAD", path), requirement);
            }
        }
    }

    public static TrustedAccessHttpRequirement GetRequirement(HttpRequest request)
        => TryGetRequirement(request, out var requirement) ? requirement : MissingPolicyRequirement;

    public static bool TryGetRequirement(HttpRequest request, out TrustedAccessHttpRequirement requirement)
    {
        // The matched catchall only returns a typed 404. It is no business read
        // and has no method restriction; new business templates still need a rule.
        if (request.HttpContext.GetEndpoint() is RouteEndpoint endpoint
            && endpoint.RoutePattern.RawText == "/api/{**unmatchedPath}")
        {
            requirement = TrustedAccessHttpRequirement.Anonymous;
            return true;
        }
        return RouteRequirements.TryGetValue((GetPolicyMethod(request), GetPolicyPath(request)), out requirement!);
    }

    private sealed class OperationComparer : IEqualityComparer<(string Method, string Path)>
    {
        public static OperationComparer Instance { get; } = new();
        public bool Equals((string Method, string Path) x, (string Method, string Path) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Method, y.Method)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);
        public int GetHashCode((string Method, string Path) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Method),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }

    public static bool TryGetRequiredScopesForSubscription(string selector, out IReadOnlyList<string> requiredScopes)
    {
        IReadOnlyList<string>? scopes = selector switch
        {
            "/api/settings/app/subscribe" => [AccessKeyScopes.Read],
              "/api/memory-protection/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/startup-modules/settings/subscribe" => [AccessKeyScopes.Read],
            "/api/metrics/subscribe" => [AccessKeyScopes.MetricsRead],
            "/api/metrics/fields/catalog/subscribe" => [],
            "/api/metrics/gpu-specialized/subscribe" => [AccessKeyScopes.MetricsRead],
            "/api/kernel-leaks/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/resource-monitor/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/resource-facts/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/performance-overlay/subscribe" => [AccessKeyScopes.MetricsRead, AccessKeyScopes.ProcessesRead],
            "/api/control/actual/subscribe" => [AccessKeyScopes.Read],
            "/api/local-system/status/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/network-endpoints/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/state/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/nvidia-drs/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/device-topology/acpi/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/topology/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/topology/fields/subscribe" => [AccessKeyScopes.Read],
            "/api/cpu/residency/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/optimization/smart/state/subscribe" => [AccessKeyScopes.ProcessesRead],
            "/api/optimization/smart/status/subscribe" => [AccessKeyScopes.Read],
            "/api/operations/subscribe" => [AccessKeyScopes.Session],
            "/api/management/inventory-revisions/subscribe" => [AccessKeyScopes.Read],
            "/api/updates/product/status/subscribe" => [AccessKeyScopes.Read],
            "/api/access-requests/subscribe" => [AccessKeyScopes.KeysManage],
            "/api/notifications/subscribe" => [AccessKeyScopes.Read],
            _ => null
        };
        requiredScopes = scopes ?? [];
        return scopes is not null;
    }

    public static bool AllowsKeylessSubscription(string selector)
        => TryGetRequiredScopesForSubscription(selector, out var scopes)
            && KeylessLocalCallerHolds(scopes);

    public static bool IsStreamingRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/subscribe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/hooks/wait", StringComparison.OrdinalIgnoreCase);
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

            // The holder rule looks at what the key carries, not at what this request needs: a key with a scope
            // whose holder must be an elevated administrator is unusable from any other process (user 10-29).
            var holderMustBeAdministrator = AccessKeyScopes.RequiresAdministratorHolder(key.Scopes);
            var administrator = holderMustBeAdministrator && administratorReader.IsAdministrator(context);
            if (holderMustBeAdministrator && !administrator)
                return TrustedAccessAuthorizationDecision.Reject(
                    StatusCodes.Status403Forbidden,
                    "elevated-administrator-required",
                    key.Id);

            if (!ContainsAll(key.Scopes, requiredScopes))
                return TrustedAccessAuthorizationDecision.Reject(
                    StatusCodes.Status403Forbidden,
                    "access-scope-required",
                    key.Id) with
                {
                    MissingScopes = requiredScopes
                        .Where(scope => !key.Scopes.Contains(scope, StringComparer.Ordinal)).ToArray()
                };

            // The holder check above does not widen what the key may do: the context names the caller an
            // administrator only on a route that itself needs an administrator holder, so granting and managing
            // keys stay bounded by the key's own scopes, as before the holder rule.
            var authorization = new TrustedAccessAuthorizationContext(key, null,
                administrator && AccessKeyScopes.RequiresAdministratorHolder(requiredScopes), false);
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

    private static TrustedAccessHttpRequirement Protected(string scope)
        => SingleScopeRequirements[scope];

    // A route admits a keyless local Windows caller exactly when the scope table gives such callers every scope
    // the route requires; no route marks this by hand.
    private static TrustedAccessHttpRequirement ScopeRequirement(string[] scopes)
        => new(TrustedAccessHttpAuthorizationMode.Protected, Array.AsReadOnly(scopes), KeylessLocalCallerHolds(scopes));

    private static bool KeylessLocalCallerHolds(IReadOnlyList<string> scopes)
        => scopes.All(TrustedAccessScopeCatalog.LocalReadScopes.Contains);

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
