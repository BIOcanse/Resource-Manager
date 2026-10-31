using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class TrustedAccessPolicyAuditTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryActuallyRegisteredEndpointHasAnExplicitMethodAndTemplatePolicy(bool full)
    {
        var capabilities = full ? StartupCapabilitySet.Full : StartupCapabilitySet.NormalReadOnly;
        var builder = WebApplication.CreateBuilder();
        LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
        await using var app = builder.Build();
        app.MapResourceManagerEndpoints(LoopbackApiPipelineMode.Administrator, capabilities);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(endpoints);
        var missing = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (endpoint.RoutePattern.RawText == "/api/{**unmatchedPath}")
            {
                // Production's only method-independent API route is the typed 404,
                // registered by the same mapper as the business endpoints above.
                Assert.True(methods is null || methods.Count == 0);
                foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" })
                {
                    var context = new DefaultHttpContext();
                    context.SetEndpoint(endpoint);
                    context.Request.Method = method;
                    Assert.Equal(TrustedAccessHttpAuthorizationMode.Anonymous,
                        TrustedAccessHttpPolicy.GetRequirement(context.Request).Mode);
                }
                continue;
            }
            Assert.NotNull(methods);
            Assert.NotEmpty(methods);
            foreach (var method in methods)
            {
                var context = new DefaultHttpContext();
                context.SetEndpoint(endpoint);
                context.Request.Method = method;
                if (!TrustedAccessHttpPolicy.TryGetRequirement(context.Request, out _))
                    missing.Add($"{method} {endpoint.RoutePattern.RawText}");
            }
        }
        Assert.True(missing.Count == 0, "Registered endpoints without explicit policy: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("GET", "/api/control/objects/{objectId}/gpu-vf-curve", AccessKeyScopes.Read)]
    [InlineData("GET", "/api/control/gpu-vf-curves", AccessKeyScopes.Read)]
    [InlineData("GET", "/api/control/gpu-vf-curves/{curveId}/references", AccessKeyScopes.Read)]
    [InlineData("PUT", "/api/control/gpu-vf-curves/{curveId}", AccessKeyScopes.Session)]
    [InlineData("DELETE", "/api/control/gpu-vf-curves/{curveId}", AccessKeyScopes.Session)]
    [InlineData("POST", "/api/control/gpu-vf-curves/editor/command", AccessKeyScopes.Session)]
    [InlineData("PUT", "/api/control/state/{objectId}", AccessKeyScopes.MachineWrite)]
    public void GpuVfReadsAndUserDataEditsKeepHardwareApplicationInTheMachineWriteScope(
        string method, string template, string expectedScope)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = template;
        Assert.True(TrustedAccessHttpPolicy.TryGetRequirement(context.Request, out var requirement));
        Assert.Equal(TrustedAccessHttpAuthorizationMode.Protected, requirement.Mode);
        Assert.Equal([expectedScope], requirement.RequiredScopes);
        Assert.Equal(expectedScope == AccessKeyScopes.Read, requirement.AllowKeylessLocalWindowsCaller);
    }

    [Fact]
    public async Task EveryRouteAndSelectorTakesItsKeylessPermissionFromTheScopeTable()
    {
        var keylessLocal = AccessKeyScopes.Definitions.Where(static scope => scope.KeylessLocal)
            .Select(static scope => scope.Name).ToHashSet(StringComparer.Ordinal);
        var holder = AccessKeyScopes.Definitions.Where(static scope => scope.RequiresAdministratorHolder)
            .Select(static scope => scope.Name).ToHashSet(StringComparer.Ordinal);
        var checkedRoutes = 0;
        foreach (var (operation, requirement) in await ProtectedProductionRequirementsAsync())
        {
            checkedRoutes++;
            if (requirement.RequiredScopes.Count == 0)
                // Keyless access requests, and the subscription envelope that authorizes each selector itself.
                Assert.True(requirement.AllowKeylessLocalWindowsCaller == requirement.RequireKeyless, operation);
            else
                Assert.True(requirement.AllowKeylessLocalWindowsCaller
                    == requirement.RequiredScopes.All(keylessLocal.Contains), operation);
            if (requirement.AllowKeylessLocalWindowsCaller)
                Assert.False(requirement.RequiredScopes.Any(holder.Contains), operation);
        }
        Assert.True(checkedRoutes > 0);

        foreach (var selector in ResourceManagerEndpointRouteBuilderExtensions.SubscriptionSelectors)
        {
            Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(selector, out var scopes), selector);
            var keyless = TrustedAccessHttpPolicy.AllowsKeylessSubscription(selector);
            Assert.True(keyless == scopes.All(keylessLocal.Contains), selector);
            if (keyless)
                Assert.False(scopes.Any(holder.Contains), selector);
        }
    }

    [Fact]
    public async Task KeylessLocalRoutesAreTheGroupsMarkedByHandBeforeTheTableDroveThem()
    {
        // Pins the 10-29 change that derives the flag from the table: no route changed. A deliberate change of the
        // table's keyless-local column updates this list with it.
        string[] earlierKeylessGroups = [AccessKeyScopes.Read, AccessKeyScopes.ProcessesRead, AccessKeyScopes.MetricsRead];
        foreach (var (operation, requirement) in await ProtectedProductionRequirementsAsync())
            Assert.True(requirement.AllowKeylessLocalWindowsCaller == (requirement.RequireKeyless
                || requirement.RequiredScopes is [var only] && earlierKeylessGroups.Contains(only)), operation);
    }

    private static async Task<List<(string Operation, TrustedAccessHttpRequirement Requirement)>>
        ProtectedProductionRequirementsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.Full);
        await using var app = builder.Build();
        app.MapResourceManagerEndpoints(LoopbackApiPipelineMode.Administrator, StartupCapabilitySet.Full);
        var requirements = new List<(string Operation, TrustedAccessHttpRequirement Requirement)>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>())
        {
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                ?? Array.Empty<string>())
            {
                var context = new DefaultHttpContext();
                context.SetEndpoint(endpoint);
                context.Request.Method = method;
                if (TrustedAccessHttpPolicy.TryGetRequirement(context.Request, out var requirement)
                    && requirement.Mode == TrustedAccessHttpAuthorizationMode.Protected)
                    requirements.Add(($"{method} {endpoint.RoutePattern.RawText}", requirement));
            }
        }
        return requirements;
    }

    [Fact]
    public void ProductUpdateStatusSubscriptionRequiresTheSameScopesAsStatusRead()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/updates/product/status";
        var read = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        Assert.Equal(TrustedAccessHttpAuthorizationMode.Protected, read.Mode);
        Assert.Equal([AccessKeyScopes.Read], read.RequiredScopes);
        Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(
            "/api/updates/product/status/subscribe", out var subscriptionScopes));
        Assert.Equal(read.RequiredScopes, subscriptionScopes);
        Assert.Equal(read.AllowKeylessLocalWindowsCaller,
            TrustedAccessHttpPolicy.AllowsKeylessSubscription("/api/updates/product/status/subscribe"));
    }

    [Fact]
    public void EverySelectorFromTheActualSubscriptionFactoryHasAnExplicitScopeRule()
    {
        var selectors = ResourceManagerEndpointRouteBuilderExtensions.SubscriptionSelectors.ToArray();
        Assert.NotEmpty(selectors);
        foreach (var selector in selectors)
        {
            Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(selector, out _),
                $"Registered subscription selector lacks policy: {selector}");
            Assert.True(ResourceManagerEndpointRouteBuilderExtensions.TryParseSubscriptionSelector(selector, out _, out _));
        }
    }

    [Theory]
    [InlineData("GET", "/api/new-unclassified-route")]
    [InlineData("POST", "/api/new-unclassified-route")]
    [InlineData("GET", "/api/resource-facts/new-operation")]
    [InlineData("POST", "/api/metrics/reset")]
    [InlineData("DELETE", "/api/access-keys/new-management-operation")]
    public void UnknownOperationsCannotInheritAnyGenericOrPrefixPolicy(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.False(TrustedAccessHttpPolicy.TryGetRequirement(context.Request, out _));
        Assert.Equal(TrustedAccessHttpAuthorizationMode.MissingPolicy,
            TrustedAccessHttpPolicy.GetRequirement(context.Request).Mode);
    }

    [Fact]
    public void ResourceFactsRequireProcessScopeForSnapshotsAndSubscriptions()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/resource-facts/snapshot";
        Assert.Equal([AccessKeyScopes.ProcessesRead], TrustedAccessHttpPolicy.GetRequirement(context.Request).RequiredScopes);
        Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/resource-facts/subscribe", out var scopes));
        Assert.Equal([AccessKeyScopes.ProcessesRead], scopes);
        Assert.False(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/missing/subscribe", out _));
        Assert.False(TrustedAccessHttpPolicy.AllowsKeylessSubscription("/api/missing/subscribe"));
    }

    [Theory]
    [InlineData("/api/debug/gpu-renderers")]
    [InlineData("/api/debug/frame-timing")]
    [InlineData("/api/debug/thread-cpu")]
    [InlineData("/api/debug/scheduling-inputs")]
    [InlineData("/api/debug/subscriptions")]
    [InlineData("/api/gpu-placement/software/{softwareId}")]
    [InlineData("/api/optimization/reports")]
    [InlineData("/api/optimization/smart/state")]
    [InlineData("/api/optimization/smart/diagnostics/decision-snapshot/subscribe")]
    [InlineData("/api/optimization/smart/validation/process-effect-scope")]
    [InlineData("/api/optimization/smart/validation/memory-cleanup-evidence")]
    public void ProcessIdentityAndProcessEvidenceRoutesRequireProcessRead(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        Assert.Equal([AccessKeyScopes.ProcessesRead], TrustedAccessHttpPolicy.GetRequirement(context.Request).RequiredScopes);
    }

    [Fact]
    public void SmartStateSelectorAndMetricCaptureRequireTheirDataScopes()
    {
        Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/optimization/smart/state/subscribe", out var scopes));
        Assert.Equal([AccessKeyScopes.ProcessesRead], scopes);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/debug/metrics/capture";
        Assert.Equal([AccessKeyScopes.MetricsRead], TrustedAccessHttpPolicy.GetRequirement(context.Request).RequiredScopes);
    }
}
