using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class LoopbackApiAuthenticationPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1:9321", null, true)]
    [InlineData("localhost:9321", "http://localhost:9321", true)]
    [InlineData("localhost:9321", "http://attacker.invalid", false)]
    [InlineData("attacker.invalid:9321", null, false)]
    [InlineData("localhost:9322", null, false)]
    [InlineData("localhost:9321", "http://localhost:9321/path", false)]
    [InlineData("localhost:9321", "http://user@localhost:9321", false)]
    public void BearerKeysDoNotRemoveHostOrOriginChecks(string host, string? origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        Assert.Equal(allowed, LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, 9321));
    }

    [Fact]
    public void MultipleOriginsAreRejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:9321");
        context.Request.Headers.Origin = new StringValues(
            ["http://localhost:9321", "http://attacker.invalid"]);
        Assert.False(LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, 9321));
    }

    [Fact]
    public void ForgedLocalhostHostDoesNotAllowNonLoopbackPeer()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:9321");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        context.Connection.LocalIpAddress = System.Net.IPAddress.Loopback;

        Assert.True(LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, 9321));
        Assert.False(LoopbackApiAuthenticationPolicy.IsLoopbackConnection(context));
    }

    [Fact]
    public void IPv4MappedLoopbackPeerRemainsLoopback()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback.MapToIPv6();
        context.Connection.LocalIpAddress = System.Net.IPAddress.IPv6Loopback;

        Assert.True(LoopbackApiAuthenticationPolicy.IsLoopbackConnection(context));
    }

    [Theory]
    [InlineData("GET", "/api/health", "Anonymous", "")]
    [InlineData("GET", "/api/debug/event-drops", "Protected", "processes.read")]
    [InlineData("GET", "/api/debug/subscriptions", "Protected", "processes.read")]
    [InlineData("GET", "/api/public/v1/capabilities", "Anonymous", "")]
    [InlineData("GET", "/api/metrics/snapshot", "Protected", "metrics.read")]
    [InlineData("HEAD", "/api/metrics/snapshot", "Protected", "metrics.read")]
    [InlineData("GET", "/api/resource-monitor/snapshot", "Protected", "processes.read")]
    [InlineData("HEAD", "/api/resource-breakdown/snapshot", "Protected", "processes.read")]
    [InlineData("GET", "/api/resource-facts/snapshot", "Protected", "processes.read")]
    [InlineData("POST", "/api/device-topology/nvidia-drs/fields/read", "Protected", "read")]
    [InlineData("POST", "/api/disk-usage/scan", "Protected", "read")]
    [InlineData("POST", "/api/components/{id}/verify", "Protected", "read")]
    [InlineData("POST", "/api/public/v1/sqlite/databases/{databaseId}/query", "Protected", "read")]
    [InlineData("GET", "/api/cpu/residency", "Protected", "processes.read")]
    [InlineData("GET", "/api/adapters/resource-manager/scheduling", "MissingPolicy", "")]
    [InlineData("POST", "/api/adapters/resource-manager/scheduling", "MissingPolicy", "")]
    [InlineData("POST", "/api/operations/{id}/cancel", "Protected", "session")]
    [InlineData("POST", "/api/optimization/reports/refresh", "Protected", "processes.read")]
    [InlineData("POST", "/api/optimization/reports/history/clear", "Protected", "session")]
    [InlineData("POST", "/api/gpu-placement/process-history/observe", "Protected", "gpu.report")]
    [InlineData("GET", "/api/targeted-recordings", "Protected", "processes.read")]
    [InlineData("HEAD", "/api/targeted-recordings", "Protected", "processes.read")]
    [InlineData("POST", "/api/targeted-recordings", "Protected", "session")]
    [InlineData("DELETE", "/api/targeted-recordings/{id}", "Protected", "session")]
    [InlineData("GET", "/api/targeted-recordings/targets", "Protected", "processes.read")]
    [InlineData("GET", "/api/targeted-recordings/items/catalog", "Protected", "processes.read")]
    [InlineData("GET", "/api/targeted-recordings/profiles", "Protected", "processes.read")]
    [InlineData("POST", "/api/targeted-recordings/profiles", "Protected", "session")]
    [InlineData("PATCH", "/api/targeted-recordings/profiles/{id}", "Protected", "session")]
    [InlineData("DELETE", "/api/targeted-recordings/profiles/{id}", "Protected", "session")]
    [InlineData("GET", "/api/startup-modules/software/{softwareId}", "Protected", "read")]
    [InlineData("HEAD", "/api/startup-modules/software/{softwareId}", "Protected", "read")]
    [InlineData("POST", "/api/system/search-online", "Protected", "session")]
    [InlineData("POST", "/api/memory-protection/normal-running-point", "Protected", "session")]
    [InlineData("PUT", "/api/memory-protection/normal-running-points/active", "Protected", "session")]
    [InlineData("DELETE", "/api/memory-protection/normal-running-points/{id}", "Protected", "session")]
    [InlineData("PATCH", "/api/memory-protection", "Protected", "machine.write")]
    [InlineData("POST", "/api/access-requests/{requestId}/approve", "Protected", "keys.manage")]
    [InlineData("POST", "/api/access-requests/{requestId}/deny", "Protected", "keys.manage")]
    [InlineData("GET", "/api/settings/app", "Protected", "read")]
    [InlineData("HEAD", "/api/settings/app", "Protected", "read")]
    [InlineData("POST", "/api/settings/app/reapply", "Protected", "machine.write")]
    [InlineData("GET", "/api/crash-reports", "Protected", "read")]
    [InlineData("POST", "/api/crash-reports/{id}/package", "Protected", "machine.write")]
    [InlineData("POST", "/api/crash-reports/{id}/issue-link", "Protected", "machine.write")]
    [InlineData("POST", "/api/crash-reports/{id}/acknowledge", "Protected", "machine.write")]
    [InlineData("DELETE", "/api/crash-reports/{id}", "Protected", "machine.write")]
    public void HttpRoutesHaveExplicitAccessRequirements(
        string method,
        string path,
        string mode,
        string scope)
    {
        foreach (var variant in MethodVariants(method))
        foreach (var matchedEndpoint in new[] { false, true })
        {
            var context = MethodRequest(variant, path, matchedEndpoint ? method : null);
            var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);

            Assert.Equal(mode, requirement.Mode.ToString());
            if (scope.Length == 0)
                Assert.Empty(requirement.RequiredScopes);
            else
                Assert.Equal(new[] { scope }, requirement.RequiredScopes);
        }
    }

    [Fact]
    public void RetiredSelfSchedulingSelectorHasNoTrustedAccessScope()
    {
        Assert.False(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(
            "/api/adapters/resource-manager/scheduling/subscribe", out var scopes));
        Assert.Empty(scopes);
        Assert.False(TrustedAccessHttpPolicy.AllowsKeylessSubscription(
            "/api/adapters/resource-manager/scheduling/subscribe"));
    }

    [Fact]
    public void UnregisteredMetricMutationsHaveNoInferredPolicy()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/metrics/reset";

        Assert.Equal(
            TrustedAccessHttpAuthorizationMode.MissingPolicy,
            TrustedAccessHttpPolicy.GetRequirement(context.Request).Mode);
    }

    [Fact]
    public async Task AnUnclassifiedApiRouteFailsClosedBeforeCallingItsHandler()
    {
        var context = MethodRequest("GET", "/api/resource-facts/new-unclassified-operation", "GET");
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 9321);
        context.Connection.LocalPort = 9321;
        context.Connection.LocalIpAddress = System.Net.IPAddress.Loopback;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        var invoked = false;
        var middleware = new LoopbackApiAuthenticationMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, new TestAdministratorReader(isAdministrator: true));

        await middleware.InvokeAsync(context, new TestKeyService(Key(AccessKeyScopes.Default)));

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("access-policy-missing", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public void RouteParameterTextCannotChangeTheCanonicalRouteScopes()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/components/open-path-injection/install";
        var endpoint = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/components/{id}/install"),
            0);
        endpoint.Metadata.Add(new HttpMethodMetadata(["POST"]));
        context.SetEndpoint(endpoint.Build());

        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);

        Assert.Equal([AccessKeyScopes.MachineWrite], requirement.RequiredScopes);
    }

    [Theory]
    [InlineData("/api/gpu-placement/process-policy")]
    [InlineData("/api/startup-modules/software/{softwareId}")]
    public async Task InjectionConfigMutationsRequireBothScopesEvenForAdministratorKeys(string path)
    {
        foreach (var method in MethodVariants("PUT"))
        foreach (var matchedEndpoint in new[] { false, true })
        {
            var context = MethodRequest(method, path, matchedEndpoint ? "PUT" : null);
            context.Request.Headers[TrustedAccessHeaders.KeyHeader] = "valid-key";
            var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
            var administratorReader = new TestAdministratorReader(isAdministrator: true);

            Assert.Equal(
                [AccessKeyScopes.MachineWrite, AccessKeyScopes.InjectionConfigure],
                requirement.RequiredScopes);
            var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
                context,
                new TestKeyService(Key([AccessKeyScopes.MachineWrite])),
                administratorReader,
                requirement.RequiredScopes,
                allowKeylessLocalWindowsCaller: false,
                requireKeyless: false,
                cancellationToken: CancellationToken.None);

            Assert.False(decision.Allowed);
            Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
            Assert.Equal("access-scope-required", decision.ErrorCode);
            // The key carries machine.write, so the holder rule reads the administrator fact before the scopes.
            Assert.Equal(1, administratorReader.CallCount);
        }
    }

    [Theory]
    [InlineData("/api/gpu-placement/process-policy")]
    [InlineData("/api/startup-modules/software/{softwareId}")]
    public async Task InjectionConfigMutationsNeedAdministratorAndAllowFullyAuthorizedKey(string path)
    {
        var context = RequestWithKey("valid-key");
        context.Request.Method = "PUT";
        context.Request.Path = path;
        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        var scopes = new[] { AccessKeyScopes.MachineWrite, AccessKeyScopes.InjectionConfigure };
        var administratorReader = new TestAdministratorReader(isAdministrator: true);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            new TestKeyService(Key(scopes)),
            administratorReader,
            requirement.RequiredScopes,
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.Equal(1, administratorReader.CallCount);
    }

    [Theory]
    [InlineData("/api/gpu-placement/process-policy")]
    [InlineData("/api/startup-modules/software/{softwareId}")]
    public async Task InjectionConfigMutationsRemainDeniedWithoutAdministrator(string path)
    {
        var context = RequestWithKey("valid-key");
        context.Request.Method = "PUT";
        context.Request.Path = path;
        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        var keyService = new TestKeyService(Key(
            [AccessKeyScopes.MachineWrite, AccessKeyScopes.InjectionConfigure]));
        var administratorReader = new TestAdministratorReader(isAdministrator: false);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            requirement.RequiredScopes,
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
        Assert.Equal("elevated-administrator-required", decision.ErrorCode);
        Assert.Equal(1, administratorReader.CallCount);
    }

    [Fact]
    public void AiCompatibilityKeepsItsPurposeSpecificAuthenticationPath()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/public/v1/ai/compat/openai/v1/chat/completions";

        Assert.True(TrustedAccessHttpPolicy.IsAiCompatibilityRequest(context.Request));
        Assert.False(TrustedAccessHttpPolicy.IsAiCompatibilityRequest(new DefaultHttpContext().Request));
    }

    [Fact]
    public async Task ValidBearerReadDoesNotRequireOrBindToWindowsIdentity()
    {
        var context = RequestWithKey("raw-test-key");
        var keyService = new TestKeyService(Key([AccessKeyScopes.MetricsRead]));
        var administratorReader = new TestAdministratorReader(isAdministrator: false);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            [AccessKeyScopes.MetricsRead],
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.Equal("key-id-1", decision.AccessKeyId);
        Assert.Equal(0, administratorReader.CallCount);
        Assert.Equal("control", keyService.LastPurpose);
        Assert.Same(keyService.Key, decision.Context!.Key);
        Assert.Null(decision.Context.WindowsCaller);
    }

    [Fact]
    public async Task ExplicitInvalidBearerCannotFallBackToAdministratorIdentity()
    {
        var context = RequestWithKey("wrong-key");
        var keyService = new TestKeyService(null);
        var administratorReader = new TestAdministratorReader(isAdministrator: true);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            [AccessKeyScopes.Read],
            allowKeylessLocalWindowsCaller: true,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, decision.StatusCode);
        Assert.Equal("access-key-invalid", decision.ErrorCode);
        Assert.Equal(0, administratorReader.CallCount);
    }

    [Fact]
    public async Task KeyStoreFailureReturnsUnavailableWithoutAdministratorFallback()
    {
        var context = RequestWithKey("valid-looking-key");
        var keyService = new TestKeyService(null)
        {
            AuthenticationFailure = new IOException("details must not escape")
        };
        var administratorReader = new TestAdministratorReader(isAdministrator: true);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            [AccessKeyScopes.Read],
            allowKeylessLocalWindowsCaller: true,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, decision.StatusCode);
        Assert.Equal("access-service-unavailable", decision.ErrorCode);
        Assert.Equal(0, administratorReader.CallCount);
    }

    [Fact]
    public async Task ElevatedScopesRequireAdministratorEvenWhenKeyGrantsThem()
    {
        var context = RequestWithKey("valid-key");
        var keyService = new TestKeyService(Key([AccessKeyScopes.MachineWrite]));
        var administratorReader = new TestAdministratorReader(isAdministrator: false);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            context,
            keyService,
            administratorReader,
            [AccessKeyScopes.MachineWrite],
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
        Assert.Equal("elevated-administrator-required", decision.ErrorCode);
        Assert.Equal(1, administratorReader.CallCount);
    }

    public static TheoryData<string> AdministratorHolderScopes()
    {
        var scopes = new TheoryData<string>();
        foreach (var scope in AccessKeyScopes.Definitions.Where(static scope => scope.RequiresAdministratorHolder))
            scopes.Add(scope.Name);
        return scopes;
    }

    [Theory]
    [MemberData(nameof(AdministratorHolderScopes))]
    public async Task KeyCarryingAnAdministratorHolderScopeIsRefusedEvenForReadsFromOtherProcesses(string scope)
    {
        var administratorReader = new TestAdministratorReader(isAdministrator: false);

        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            RequestWithKey("valid-key"),
            new TestKeyService(Key([AccessKeyScopes.Read, scope])),
            administratorReader,
            [AccessKeyScopes.Read],
            allowKeylessLocalWindowsCaller: true,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
        Assert.Equal("elevated-administrator-required", decision.ErrorCode);
        Assert.Equal(1, administratorReader.CallCount);
    }

    [Fact]
    public async Task KeyCarryingAnAdministratorHolderScopeReadsFromAnElevatedAdministrator()
    {
        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            RequestWithKey("valid-key"),
            new TestKeyService(Key([AccessKeyScopes.Read, AccessKeyScopes.MachineWrite])),
            new TestAdministratorReader(isAdministrator: true),
            [AccessKeyScopes.Read],
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.True(decision.Allowed);
        // The read route needs no administrator holder, so the verified administrator is not carried into the
        // context: granting and managing keys stay bounded by this key's scopes (review M1).
        Assert.False(decision.Context!.IsAdministrator);
    }

    [Fact]
    public async Task AdministratorHolderRouteCarriesTheVerifiedAdministratorIntoTheContext()
    {
        var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
            RequestWithKey("valid-key"),
            new TestKeyService(Key([AccessKeyScopes.MachineWrite])),
            new TestAdministratorReader(isAdministrator: true),
            [AccessKeyScopes.MachineWrite],
            allowKeylessLocalWindowsCaller: false,
            requireKeyless: false,
            cancellationToken: CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.True(decision.Context!.IsAdministrator);
    }

    [Fact]
    public async Task KeyHoldingOnlyInjectionConfigureIsRefusedAsAHolderBeforeTheScopeCheck()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "PUT";
        context.Request.Path = "/api/gpu-placement/process-policy";
        var requiredScopes = TrustedAccessHttpPolicy.GetRequirement(context.Request).RequiredScopes;
        var key = new TestKeyService(Key([AccessKeyScopes.InjectionConfigure]));

        var fromUser = await TrustedAccessHttpPolicy.AuthorizeAsync(RequestWithKey("valid-key"), key,
            new TestAdministratorReader(isAdministrator: false), requiredScopes,
            allowKeylessLocalWindowsCaller: false, requireKeyless: false, cancellationToken: CancellationToken.None);
        var fromAdministrator = await TrustedAccessHttpPolicy.AuthorizeAsync(RequestWithKey("valid-key"), key,
            new TestAdministratorReader(isAdministrator: true), requiredScopes,
            allowKeylessLocalWindowsCaller: false, requireKeyless: false, cancellationToken: CancellationToken.None);

        // Review I2: an unelevated holder learns only that it must be an administrator, not which scope is missing.
        Assert.Equal(StatusCodes.Status403Forbidden, fromUser.StatusCode);
        Assert.Equal("elevated-administrator-required", fromUser.ErrorCode);
        Assert.Empty(fromUser.MissingScopes);
        Assert.Equal(StatusCodes.Status403Forbidden, fromAdministrator.StatusCode);
        Assert.Equal("access-scope-required", fromAdministrator.ErrorCode);
        Assert.Equal([AccessKeyScopes.MachineWrite], fromAdministrator.MissingScopes);
    }

    [Fact]
    public void ScopeTableKeepsAdministratorHolderScopesFromUnelevatedHolders()
    {
        // The bundled UI holds the default key and runs unelevated; keyless local callers are unelevated too.
        Assert.DoesNotContain(AccessKeyScopes.Definitions,
            static scope => scope.RequiresAdministratorHolder && (scope.InDefaultKey || scope.KeylessLocal));
        Assert.False(AccessKeyScopes.RequiresAdministratorHolder(AccessKeyScopes.Default));
        Assert.False(AccessKeyScopes.RequiresAdministratorHolder(TrustedAccessScopeCatalog.LocalReadScopes));
        Assert.Equal(AccessKeyScopes.Definitions.Count,
            AccessKeyScopes.Definitions.Select(static scope => scope.Name).Distinct(StringComparer.Ordinal).Count());
        // The holder check answers each row's column, so changing the column alone changes the rule.
        foreach (var scope in AccessKeyScopes.Definitions)
            Assert.True(AccessKeyScopes.RequiresAdministratorHolder([scope.Name]) == scope.RequiresAdministratorHolder,
                scope.Name);
    }

    [Theory]
    [InlineData("POST", "/api/optimization/reports/{id}/dismiss")]
    [InlineData("DELETE", "/api/optimization/trust/{id}")]
    [InlineData("PUT", "/api/control/presets/{objectId}/{name}")]
    [InlineData("DELETE", "/api/control/presets/{presetId}")]
    [InlineData("DELETE", "/api/control/instances/{instanceId}")]
    [InlineData("POST", "/api/software/manual")]
    [InlineData("DELETE", "/api/software/manual/{id}")]
    [InlineData("POST", "/api/software/portable/root")]
    public void RecordOnlyActionsAreSessionActions(string method, string path)
        => Assert.Equal([AccessKeyScopes.Session],
            TrustedAccessHttpPolicy.GetRequirement(MethodRequest(method, path, method).Request).RequiredScopes);

    [Fact]
    public void AccessRequestCreateAndWaitMustBeKeyless()
    {
        foreach (var method in MethodVariants("POST"))
        foreach (var path in new[] { "/api/access-requests", "/api/access-requests/{requestId}/wait" })
        foreach (var matchedEndpoint in new[] { false, true })
            Assert.True(TrustedAccessHttpPolicy.GetRequirement(
                MethodRequest(method, path, matchedEndpoint ? "POST" : null).Request).RequireKeyless);
    }

    private static string[] MethodVariants(string method)
        => [method, method.ToLowerInvariant(), method[..1] + method[1..].ToLowerInvariant()];

    private static DefaultHttpContext MethodRequest(string method, string path, string? matchedMethod)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (matchedMethod is not null)
        {
            var endpoint = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(path), 0);
            endpoint.Metadata.Add(new HttpMethodMetadata([matchedMethod]));
            context.SetEndpoint(endpoint.Build());
        }
        return context;
    }

    private static DefaultHttpContext RequestWithKey(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TrustedAccessHeaders.KeyHeader] = key;
        return context;
    }

    private static AccessKeySummary Key(string[] scopes)
        => new("key-id-1", "test", AccessKeyPurposes.Control, scopes, DateTimeOffset.UnixEpoch, false);

    private sealed class TestAdministratorReader(bool isAdministrator) : ILoopbackApiAdministratorReader
    {
        public int CallCount { get; private set; }

        public bool IsAdministrator(HttpContext context)
        {
            CallCount++;
            return isAdministrator;
        }
    }

    private sealed class TestKeyService(AccessKeySummary? key) : ITrustedAccessKeyService
    {
        public AccessKeySummary? Key { get; } = key;
        public string? LastPurpose { get; private set; }
        public Exception? AuthenticationFailure { get; init; }

        public Task<IReadOnlyList<AccessKeySummary>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AccessKeySummary>>([]);

        public Task<AccessKeyCreated> CreateAsync(AccessKeyCreateRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AccessKeyCreated> RotateAsync(string credentialId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RevokeAsync(string credentialId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AccessKeyCreated?> SetDefaultAsync(bool enabled, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AccessKeySummary?> AuthenticateAsync(
            string purpose,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            LastPurpose = purpose;
            if (AuthenticationFailure is not null)
                return Task.FromException<AccessKeySummary?>(AuthenticationFailure);
            return Task.FromResult(Key);
        }

        public Task InitializeAsync(bool recover, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<bool> ValidateCredentialAsync(string credentialId, CancellationToken cancellationToken)
            => ValueTask.FromResult(Key?.Id == credentialId);

        public CancellationToken GetRevocationToken(string credentialId)
            => CancellationToken.None;
    }
}
