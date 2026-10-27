using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions ConditionJsonOptions = new(JsonSerializerDefaults.Web)
    { TypeInfoResolver = BackendHttpJsonContext.Default };
    private sealed record MonitoringConditionInput(string Id, string Condition);

    private static Task<ExpandedMonitoringHook> ExpandMonitoringConditionAsync(string text, string id,
        IServiceProvider services, CancellationToken cancellationToken)
        => MonitoringConditionLanguage.ExpandAsync(text, id,
            MonitoringConditionVocabulary.Create(services.GetService<HardwareMetricCatalogState>()?.Current),
            pid => ResolveConditionProcessStartKeyAsync(pid, id, services, cancellationToken));

    private static async Task<string?> ResolveConditionProcessStartKeyAsync(int pid, string id,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var resources = services.GetRequiredService<IResourceBreakdownPushSource>();
        var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(),
            ProcessSampleDetailLevel.ResourceTableBasic)
            { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] };
        if (await resources.FetchOnceAsync($"monitoring-condition:{id}:process-identity", request, cancellationToken) is null)
            return null;
        var current = services.GetRequiredService<ISchedulingProcessFactObservationSource>().ReadLatest(new(
            SchedulingProcessMetricMask.None, new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
        if (current?.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _) != true) return null;
        var process = current.InventoryProcesses.FirstOrDefault(item => item.ProcessId == pid && item.ProcessStartKey > 0);
        return process?.ProcessStartKey.ToString(CultureInfo.InvariantCulture);
    }

    private static IEndpointRouteBuilder MapMonitoringConditionEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hooks/vocabulary", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(MonitoringConditionVocabulary.Create(
                context.RequestServices.GetService<HardwareMetricCatalogState>()?.Current), ConditionJsonOptions);
        }).AllowAnonymous();
        app.MapGet("/api/hooks/wait", RunMonitoringConditionWaitAsync).AllowAnonymous();
        return app;
    }

    private static async Task RunMonitoringConditionWaitAsync(HttpContext context, IOptions<JsonOptions> options,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Resource-Manager-Key-Header"] = "X-Resource-Manager-Key";
        response.Headers["X-Resource-Manager-Authentication"] = "trusted-key-or-verified-loopback-Windows-caller";
        if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue("condition", out var input)
            || input.Count != 1 || input[0] is null)
        {
            await ErrorAsync(new(400, "Supply exactly one condition query value.")); return;
        }
        var json = JsonSerializer.SerializeToElement(new[] { new MonitoringConditionInput("condition", input[0]!) }, ConditionJsonOptions);
        var compilation = await CompileSubscriptionChannelRequestAsync(new(1, null, json), context);
        if (!compilation.Succeeded)
        { await ErrorAsync(new(compilation.StatusCode, compilation.Error)); return; }
        if (compilation.HookRejections is { Count: > 0 })
        { await ErrorAsync(compilation.HookRejections[0].Value); return; }
        var expanded = compilation.HookExpansions![0].Value.Hook;
        response.Headers["X-Resource-Manager-Expanded-Hook"] = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(expanded, ConditionJsonOptions));
        var selector = MonitoringHookAuthorizationSelector(expanded.Rule.Binding, expanded.Rule.TopOwners);
        TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(selector, out var scopes);
        response.Headers["X-Resource-Manager-Required-Scopes"] = string.Join(',', scopes);
        var authorization = context.Items.TryGetValue(typeof(TrustedAccessAuthorizationContext), out var authorizationObject)
            ? authorizationObject as TrustedAccessAuthorizationContext : null;
        var keyService = context.RequestServices.GetRequiredService<ITrustedAccessKeyService>();
        CancellationToken revoked;
        try { revoked = authorization?.Key is { } key ? keyService.GetRevocationToken(key.Id) : CancellationToken.None; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("ResourceManager.MonitoringCondition").LogWarning(error, "Hook credential state is unavailable.");
            await ErrorAsync(new(503, "access-service-unavailable")); return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        using var receiver = new FrontendSubscriptionChannelWriter(response, options.Value.SerializerOptions,
            authorization?.Key is { } activeKey ? token => keyService.ValidateCredentialAsync(activeKey.Id, token) : null,
            async (id, value, token) =>
            {
                // This is a one-result HTTP consumer of the unchanged low-level hook.
                // It does not evaluate the condition or retain a second rule state.
                if (value is SubscriptionChannelRejection rejection) await ErrorAsync(rejection);
                else if (value is MonitoringHookEvent { State: "met" or "objectEnded" } result)
                {
                    response.StatusCode = result.State == "met" ? 200 : 409;
                    await response.WriteAsJsonAsync(new MonitoringHookWaitResult(id, expanded, result), options.Value.SerializerOptions, token);
                }
                else return;
                await lifetime.CancelAsync();
            });
        try { await compilation.Subscriptions[0].RunAsync(receiver, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            loggerFactory.CreateLogger("ResourceManager.MonitoringCondition").LogWarning(error, "Hook wait channel failed.");
            if (!response.HasStarted && !cancellationToken.IsCancellationRequested) await ErrorAsync(new(503, "hook-channel-error"));
            return;
        }
        if (!response.HasStarted && !cancellationToken.IsCancellationRequested)
            await ErrorAsync(new(revoked.IsCancellationRequested ? 403 : 503,
                revoked.IsCancellationRequested ? "credential-revoked" : "hook-ended-before-met"));

        async Task ErrorAsync(SubscriptionChannelRejection rejection)
        {
            response.StatusCode = rejection.StatusCode;
            await response.WriteAsJsonAsync(rejection, options.Value.SerializerOptions, cancellationToken);
        }
    }
}
