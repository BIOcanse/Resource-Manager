using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.Runtime.TrustedAccess;
using ResourceManager.App.Infrastructure.Updates;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private const int SubscriptionChannelMaximumItemCount = 64;
    private const int SubscriptionChannelMaximumIdLength = 128;
    private const int SubscriptionChannelMaximumPathLength = 8_192;
    private const long SubscriptionChannelMaximumBodyLength = 600L * 1_024L;

    private static IEndpointRouteBuilder MapSubscriptionChannelEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/subscriptions/stream", async (
            FrontendSubscriptionChannelRequest? request,
            HttpContext httpContext,
            IOptions<JsonOptions> jsonOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var compilation = await CompileSubscriptionChannelRequestAsync(request, httpContext);
            if (!compilation.Succeeded)
            {
                httpContext.Response.StatusCode = compilation.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(
                    new EndpointErrorPayload(compilation.Error),
                    jsonOptions.Value.SerializerOptions,
                    cancellationToken);
                return;
            }
            var subscriptions = compilation.Subscriptions;

            var authorization = httpContext.Items.TryGetValue(
                    typeof(TrustedAccessAuthorizationContext),
                    out var authorizationObject)
                ? authorizationObject as TrustedAccessAuthorizationContext
                : null;
            var revocationToken = CancellationToken.None;
            if (authorization?.Key is { } authenticatedKey)
            {
                try
                {
                    revocationToken = httpContext.RequestServices
                        .GetRequiredService<ITrustedAccessKeyService>()
                        .GetRevocationToken(authenticatedKey.Id);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger("ResourceManager.TrustedAccessAuthorization")
                        .LogWarning("Trusted access stream revocation state is unavailable ({ErrorType}).",
                            error.GetType().Name);
                    httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await httpContext.Response.WriteAsJsonAsync(
                        new EndpointErrorPayload("access-service-unavailable"),
                        jsonOptions.Value.SerializerOptions,
                        cancellationToken);
                    return;
                }
            }

            NdjsonResponse.Prepare(httpContext.Response);
            using var revocationCancellation = authorization?.Key is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    revocationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var channelCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(revocationCancellation.Token);
            using var writer = new FrontendSubscriptionChannelWriter(
                httpContext.Response,
                jsonOptions.Value.SerializerOptions,
                authorization?.Key is { } streamKey
                    ? token => httpContext.RequestServices.GetRequiredService<ITrustedAccessKeyService>()
                        .ValidateCredentialAsync(streamKey.Id, token)
                    : null);
            foreach (var rejection in compilation.Rejections)
                await writer.WriteAsync(rejection.SubscriptionId, rejection.Value, channelCancellation.Token);
            foreach (var rejection in compilation.HookRejections ?? [])
                await writer.WriteHookAsync(rejection, channelCancellation.Token);
            foreach (var expansion in compilation.HookExpansions ?? [])
                await writer.WriteHookAsync(expansion, channelCancellation.Token);
            var logger = loggerFactory.CreateLogger(
                "ResourceManager.FrontendSubscriptionChannel");
            var workers = subscriptions
                .Select(subscription => RunSubscriptionChannelWorkerAsync(
                    subscription,
                    writer,
                    channelCancellation,
                    logger,
                    httpContext.TraceIdentifier))
                .ToArray();

            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                await channelCancellation.CancelAsync();
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (OperationCanceledException)
                    when (channelCancellation.IsCancellationRequested)
                {
                }
            }
        }).AllowAnonymous().WithMetadata(
            new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                SubscriptionChannelMaximumBodyLength));

        return MapMonitoringConditionEndpoints(app);
    }

    internal static async Task RunSubscriptionChannelWorkerAsync(
        CompiledFrontendSubscription subscription,
        FrontendSubscriptionChannelWriter writer,
        CancellationTokenSource channelCancellation,
        ILogger logger,
        string traceIdentifier)
    {
        try
        {
            await subscription.RunAsync(writer, channelCancellation.Token);
        }
        catch (OperationCanceledException)
            when (channelCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (FrontendSubscriptionChannelWriteException exception)
        {
            logger.LogWarning(
                exception.InnerException ?? exception,
                "Frontend callback transport write failed for {SubscriptionId} ({Selector}) on {TraceIdentifier}.",
                subscription.Id,
                subscription.Selector,
                traceIdentifier);
            await channelCancellation.CancelAsync();
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Frontend logical callback ended for {SubscriptionId} ({Selector}) on {TraceIdentifier}; closing the stream for reconnection.",
                subscription.Id,
                subscription.Selector,
                traceIdentifier);
            await channelCancellation.CancelAsync();
            return;
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, channelCancellation.Token);
        }
        catch (OperationCanceledException)
            when (channelCancellation.IsCancellationRequested)
        {
        }
    }

    internal static async Task<SubscriptionChannelCompilationResult> CompileSubscriptionChannelRequestAsync(
        FrontendSubscriptionChannelRequest? request,
        HttpContext httpContext)
    {
        var statusCode = StatusCodes.Status400BadRequest;
        if (request is null || request.Version != 1)
        {
            return Failed(statusCode, "Subscription channel version 1 is required.");
        }
        if ((request.Subscriptions?.Count ?? 0) > SubscriptionChannelMaximumItemCount
            || (request.Subscriptions?.Count ?? 0) == 0 && request.Hooks.ValueKind == JsonValueKind.Undefined)
        {
            return Failed(statusCode, $"The subscription count must be from 1 through {SubscriptionChannelMaximumItemCount}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new List<ParsedFrontendSubscription>(
            request.Subscriptions?.Count ?? 0);
        var rejections = new List<FrontendSubscriptionChannelFrame<SubscriptionChannelRejection>>();
        var hookRejections = new List<MonitoringHookFrame<SubscriptionChannelRejection>>();
        foreach (var item in request.Subscriptions ?? [])
        {
            if (item is null
                || !IsValidSubscriptionId(item.Id)
                || !ids.Add(item.Id!))
            {
                return Failed(statusCode, "Every subscription must have a unique valid id.");
            }
            if (!TryParseSubscriptionSelector(
                item.Path,
                out var selector,
                out var query))
            {
                rejections.Add(new(item.Id!, new(StatusCodes.Status400BadRequest, "unsupported-subscription-selector")));
                continue;
            }
            if (!TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(selector, out _))
            {
                rejections.Add(new(item.Id!, new(StatusCodes.Status503ServiceUnavailable, "access-policy-missing")));
                continue;
            }
            descriptors.Add(new ParsedFrontendSubscription(
                item.Id!,
                selector,
                query));
        }

        var keyService = httpContext.RequestServices.GetRequiredService<ITrustedAccessKeyService>();
        var administratorReader = httpContext.RequestServices.GetRequiredService<ILoopbackApiAdministratorReader>();
        var authorizationLogger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ResourceManager.TrustedAccessAuthorization");
        // PID shorthand expansion reads protected published process identity.
        // Authenticate that read before invoking the pure expander's resolver.
        if (request.Hooks.ValueKind == JsonValueKind.Array && request.Hooks.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("condition", out var condition)
            && condition.ValueKind == JsonValueKind.String && condition.GetString()!.TrimStart(' ').StartsWith("process ", StringComparison.Ordinal)))
        {
            TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/resource-monitor/subscribe", out var identityScopes);
            var identityAccess = await TrustedAccessHttpPolicy.AuthorizeAsync(httpContext, keyService, administratorReader,
                identityScopes, TrustedAccessHttpPolicy.AllowsKeylessSubscription("/api/resource-monitor/subscribe"), false, httpContext.RequestAborted);
            authorizationLogger.LogInformation("Trusted access key {AccessKeyId} operation {Operation} result {Result}",
                identityAccess.AccessKeyId ?? "none", "/api/resource-monitor/subscribe",
                identityAccess.Allowed ? "allowed" : $"denied:{identityAccess.ErrorCode}");
            if (!identityAccess.Allowed) return Failed(identityAccess.StatusCode, identityAccess.ErrorCode);
        }
        var hooks = await DecodeMonitoringHooksAsync(request.Hooks, hookRejections,
            httpContext.RequestServices, httpContext.RequestAborted);
        foreach (var descriptor in descriptors.Concat(hooks.Select(hook =>
            new ParsedFrontendSubscription(hook.Id, hook.AuthorizationSelector, QueryCollection.Empty))))
        {
            TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription(descriptor.Selector, out var requiredScopes);
            var hasExplicitKey = httpContext.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader);
            if (requiredScopes.Count == 0 && !hasExplicitKey)
            {
                authorizationLogger.LogInformation(
                    "Trusted access key {AccessKeyId} operation {Operation} result {Result}",
                    "none",
                    descriptor.Selector,
                    "allowed:anonymous");
                continue;
            }

            var decision = await TrustedAccessHttpPolicy.AuthorizeAsync(
                httpContext,
                keyService,
                administratorReader,
                requiredScopes,
                TrustedAccessHttpPolicy.AllowsKeylessSubscription(descriptor.Selector),
                requireKeyless: false,
                httpContext.RequestAborted);
            authorizationLogger.LogInformation(
                "Trusted access key {AccessKeyId} operation {Operation} result {Result}",
                decision.AccessKeyId ?? "none",
                descriptor.Selector,
                decision.Allowed ? "allowed" : $"denied:{decision.ErrorCode}");
            if (!decision.Allowed)
                return Failed(decision.StatusCode, decision.ErrorCode);
        }

        try
        {
            var subscriptions = descriptors
                .Select(descriptor => CompileFrontendSubscription(
                    descriptor,
                    httpContext.RequestServices))
                .ToList();
            var expansions = new List<MonitoringHookFrame<MonitoringHookExpansion>>();
            foreach (var hook in hooks)
            {
                try
                {
                    subscriptions.Add(CompileMonitoringHook(hook, httpContext.RequestServices));
                    if (hook.Expansion is { } expanded) expansions.Add(new(hook.Id, new("expanded", expanded)));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
                { hookRejections.Add(new(hook.Id, new(400, error.Message))); }
            }
            return new(subscriptions, StatusCodes.Status200OK, string.Empty, rejections, hookRejections, expansions);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or FormatException
                or OverflowException)
        {
            return Failed(StatusCodes.Status400BadRequest, "One or more subscription queries are invalid.");
        }
        catch (InvalidOperationException)
        {
            return Failed(StatusCodes.Status503ServiceUnavailable, "One or more subscription sources are unavailable.");
        }

        static SubscriptionChannelCompilationResult Failed(int code, string message)
            => new([], code, message, []);
    }

    /// <summary>
    /// 统一订阅源的**路由表**：一条选择器对应一条订阅源，唯一一条。
    ///
    /// **准入和派发是同一张表的两次查询，不是两份名单。**
    /// 先前这两件事各有一份选择器清单（一份用来判断路径合不合法，一份用来决定调谁），
    /// 加一条订阅源要同时改两处 —— 漏改一处的结果是路径被判为不支持，
    /// 而派发那边明明写着它。这种错只会在运行时露面。
    ///
    /// 所以：**一级路由**就是拿选择器查这张表，查得到即合法、且已经确定了是哪条源；
    /// **二级路由**是那条源自己按 query 决定采什么（哪个指标、哪块设备），
    /// 那是它自己的事，不在这一层展开。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SubscriptionSourceRoute>
        SubscriptionSourceRoutes = new Dictionary<string, SubscriptionSourceRoute>(
            StringComparer.Ordinal)
        {
            ["/api/memory-protection/subscribe"] = new((subscription, services, _) =>
                subscription.Compile(async (writer, token) =>
                {
                    await foreach (var value in services.GetRequiredService<ResourceManager.App.Infrastructure.MemoryProtection.HostMemoryProtectionService>().SubscribeAsync(token))
                        await writer.WriteAsync(subscription.Id, value.Status, token);
                })),
            ["/api/settings/app/subscribe"] = new((subscription, services, _) =>
                CompileSettingsSubscription(subscription, services.GetRequiredService<IAppSettingsSubscriptionSource>().SubscribeAsync)),
            ["/api/startup-modules/settings/subscribe"] = new((subscription, services, _) =>
                CompileSettingsSubscription(subscription, services.GetRequiredService<IStartupModuleSettingsSubscriptionSource>().SubscribeAsync)),
            ["/api/metrics/subscribe"] = new((subscription, services, interval) =>
                CompileMetricSubscription(subscription, services, interval)),
            ["/api/metrics/fields/catalog/subscribe"] = new((subscription, services, interval) =>
                CompileMetricCatalogSubscription(subscription, services, interval)),
            ["/api/metrics/gpu-specialized/subscribe"] = new((subscription, services, interval) =>
                CompileGpuSubscription(subscription, services, interval)),
            ["/api/kernel-leaks/subscribe"] = new((subscription, services, _) =>
                subscription.Compile(async (writer, token) =>
                {
                    await foreach (var report in services.GetRequiredService<Application.KernelLeak.IKernelLeakReportSource>().SubscribeAsync(token))
                        await writer.WriteAsync(subscription.Id, report, token);
                })),
            ["/api/resource-monitor/subscribe"] = new((subscription, services, interval) =>
                CompileResourceSubscription(subscription, services, interval)),
            ["/api/resource-facts/subscribe"] = new((subscription, services, interval) =>
                CompileResourceFactsSubscription(subscription, services, interval)),
            ["/api/performance-overlay/subscribe"] = new((subscription, services, interval) =>
                CompilePerformanceOverlaySubscription(subscription, services, interval)),
            ["/api/adapters/resource-manager/scheduling/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicSubscription(
                    subscription,
                    services.GetRequiredService<IResourceManagerSelfSchedulingControl>()
                        .GetSchedulingSnapshot,
                    interval)),
            // 控制面的实际状态：这台机器现在实际是什么样。
            // 通道存活期间持有采样需求；发布只取当前值，不直接触碰硬件。
            ["/api/control/actual/subscribe"] =
                new((subscription, services, interval) => CompileControlActualSubscription(
                    subscription,
                    services.GetRequiredService<IControlActualStateOwner>(),
                    interval)),
            ["/api/local-system/status/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicSubscription(
                    subscription,
                    services.GetRequiredService<ILocalSystemStatusProvider>().GetStatus,
                    interval)),
            ["/api/device-topology/state/subscribe"] =
                new((subscription, services, interval) => CompileDeviceTopologySubscription(
                    subscription,
                    services.GetRequiredService<IDeviceTopologySnapshotProvider>(),
                    interval)),
            ["/api/device-topology/fields/subscribe"] =
                new((subscription, services, interval) => CompileDeviceTopologySubscription(
                    subscription,
                    services.GetRequiredService<IDeviceTopologySnapshotProvider>(),
                    interval,
                    fieldsOnly: true)),
            ["/api/device-topology/nvidia-drs/fields/subscribe"] =
                new((subscription, services, interval) => CompileNvidiaDrsSubscription(subscription, services, interval)),
            ["/api/device-topology/acpi/fields/subscribe"] =
                new((subscription, services, interval) => CompileAcpiInformationSubscription(
                    subscription,
                    services.GetRequiredService<IDeviceTopologySnapshotProvider>(),
                    interval)),
            ["/api/cpu/topology/subscribe"] =
                new((subscription, services, interval) => CompileCpuTopologySubscription(
                    subscription,
                    services.GetRequiredService<ICpuTopologyReader>(),
                    interval)),
            ["/api/cpu/topology/fields/subscribe"] =
                new((subscription, services, interval) => CompileCpuTopologySubscription(
                    subscription,
                    services.GetRequiredService<ICpuTopologyReader>(),
                    interval,
                    fieldsOnly: true)),
            ["/api/cpu/residency/subscribe"] =
                new((subscription, services, interval) => CompileCpuResidencySubscription(
                    subscription,
                    services.GetRequiredService<ICpuCoreResidencyReader>(),
                    interval)),
            ["/api/optimization/smart/state/subscribe"] =
                new((subscription, services, interval) =>
                {
                    var coordinator = services.GetRequiredService<IHostManagerSmartCoordinator>();
                    return CompileAlignedPeriodicAsyncSubscription(subscription,
                        cancellationToken => HostManagerSmartStateWireProjection.ReadAsync(coordinator, cancellationToken), interval);
                }),
            // 优化页的调度状态：模式、是否运行、故障关闭、待确认与已优化对象数都由协调器一次合成。
            ["/api/optimization/smart/status/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicAsyncSubscription(
                    subscription,
                    services.GetRequiredService<IHostManagerSmartCoordinator>().GetStatusAsync,
                    interval)),
            // 操作队列不按间隔采样：它是推来的，来一条发一条。
            ["/api/updates/product/status/subscribe"] = new((subscription, services, _) =>
                subscription.Compile(async (writer, token) =>
                {
                    await foreach (var status in services.GetRequiredService<ProductUpdateCoordinator>().SubscribeAsync(token))
                        await writer.WriteAsync(subscription.Id, status, token);
                })),
            ["/api/operations/subscribe"] = new((subscription, services, _) =>
                CompileOperationSubscription(
                    subscription,
                    services.GetService<IHostManagerOperationQueryService>())),
            ["/api/management/inventory-revisions/subscribe"] = new((subscription, services, _) =>
                CompileManagementInventoryRevisionSubscription(
                    subscription,
                    services.GetRequiredService<IManagementInventoryRevisionOwner>())),
            ["/api/access-requests/subscribe"] = new((subscription, services, _) =>
                CompileAccessRequestSubscription(
                    subscription,
                    services.GetRequiredService<ITrustedAccessRequestOwner>()))
        };

    internal static IEnumerable<string> SubscriptionSelectors => SubscriptionSourceRoutes.Keys;

    private static CompiledFrontendSubscription CompileSettingsSubscription<T>(
        ParsedFrontendSubscription subscription, Func<string, CancellationToken, IAsyncEnumerable<T>> subscribe)
    {
        return subscription.Compile(async (writer, token) =>
        {
            var holder = $"settings:{subscription.Id}:{Guid.NewGuid():N}";
            await foreach (var value in subscribe(holder, token))
                await writer.WriteAsync(subscription.Id, value, token);
        });
    }

    /// <summary>
    /// 一条订阅源：拿到这次订阅的 query 和间隔之后，怎么把它编成一个运行体。
    /// 选择器不在这里 —— 它是表的键，放两份就又有了漂移的机会。
    /// </summary>
    private sealed record SubscriptionSourceRoute(
        Func<ParsedFrontendSubscription, IServiceProvider, TimeSpan, CompiledFrontendSubscription>
            Compile);

    private static CompiledFrontendSubscription CompileFrontendSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services)
    {
        var interval = ParseSamplingSubscriptionInterval(subscription.Query);
        // 能走到这里就说明一级路由已经查到过它了（准入用的是同一张表）。
        return SubscriptionSourceRoutes[subscription.Selector]
            .Compile(subscription, services, interval);
    }

    private static CompiledFrontendSubscription CompileMetricCatalogSubscription(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        var source = services.GetRequiredService<IMetricSnapshotPushSource>();
        var catalog = services.GetRequiredService<HardwareMetricCatalogState>();
        return subscription.Compile((writer, cancellationToken) => RunMetricCatalogSubscriptionAsync(
            subscription.Id, source, catalog, interval, services, writer, cancellationToken));
    }

    private static CompiledFrontendSubscription CompileMetricSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services,
        TimeSpan interval)
    {
        var useFields = subscription.Query.ContainsKey("device") || subscription.Query.ContainsKey("fields");
        var selections = useFields ? ParseMetricFieldSelections(subscription.Query) : [];
        var snapshotRequest = useFields
            ? MetricSampleRequest.Merge(selections.Select(item => MetricDeviceFields.CreateRequest(
                services.GetRequiredService<HardwareMetricCatalogState>().Current, item.Device, item.Fields)))
            : subscription.Query.TryGetValue("ids", out var ids)
            ? MetricSampleRequest.ForIds(ids)
            : MetricSampleRequest.ForIds([]);
        var source = services.GetRequiredService<IMetricSnapshotPushSource>();
        var catalogState = services.GetRequiredService<HardwareMetricCatalogState>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            if (useFields)
            {
                await RunMetricFieldSubscriptionAsync(subscription.Id, source, catalogState,
                    selections, interval, services, writer, cancellationToken);
                return;
            }
            await foreach (var snapshot in source.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                snapshotRequest,
                interval,
                cancellationToken))
            {
                await writer.WriteAsync(
                    subscription.Id,
                    MetricSnapshotWireSnapshot.From(snapshot, snapshotRequest, catalogState.Revision),
                    cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileGpuSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services,
        TimeSpan interval)
    {
        var counterIds = subscription.Query.TryGetValue("ids", out var ids)
            ? ids.Select(static id => id ?? string.Empty)
            : [];
        var request = GpuTelemetryWorkerRequest.Create(
            counterIds,
            GpuTelemetryWorkerDetailLevel.AdvancedCounters,
            timeoutMilliseconds: 500);
        var source = services.GetRequiredService<IGpuTelemetryPushSource>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in source.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                request,
                interval,
                cancellationToken))
            {
                await writer.WriteAsync(
                    subscription.Id,
                    GpuTelemetryWireSnapshot.From(snapshot),
                    cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileResourceSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services,
        TimeSpan interval)
    {
        var scope = ParseResourceMonitorScope(subscription.Query);
        var metricIds = ParseResourceMetricIds(subscription.Query);
        var scaleModes = ParseResourceScaleModes(subscription.Query);
        var processDetailSoftwareIds =
            ParseResourceBreakdownProcessDetailSoftwareIds(subscription.Query);
        var measures = ParseResourceBarMeasures(subscription.Query);
        var residualDetailMetricIds = ParseResourceResidualDetailMetricIds(subscription.Query);
        metricIds = metricIds.Concat(measures).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sampleRequest = new ResourceBreakdownSampleRequest(
            metricIds, scaleModes, ProcessSampleDetailLevel.ResourceTableBasic)
        { ProcessMemoryFactIds = measures };
        sampleRequest = SelectPhysicalResidualDetails(sampleRequest, scope, residualDetailMetricIds);
        var source = services.GetRequiredService<IResourceBreakdownPushSource>();
        if (scope == ResourceMonitorScope.Details)
        {
            return subscription.Compile(async (writer, cancellationToken) =>
            {
                await foreach (var sampledBreakdown in source.SubscribeAsync(
                    $"frontend-channel:{subscription.Id}",
                    sampleRequest,
                    interval,
                    cancellationToken))
                {
                    await writer.WriteAsync(
                        subscription.Id,
                        ResourceProcessDetailsWireSnapshot.Create(
                            sampledBreakdown,
                            metricIds,
                            processDetailSoftwareIds, ReadResourceFoundations(sampleRequest, services), scaleModes, measures, residualDetailMetricIds),
                        cancellationToken);
                }
            });
        }

        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var sampledBreakdown in source.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                sampleRequest,
                interval,
                cancellationToken))
            {
                var breakdownWire = ResourceBreakdownWireSnapshot.Create(
                    sampledBreakdown,
                    metricIds,
                    processDetailSoftwareIds,
                    ReadResourceFoundations(sampleRequest, services), scaleModes, measures, residualDetailMetricIds);
                await writer.WriteAsync(
                    subscription.Id,
                    new ResourceMonitorWireSnapshot(
                        ResourceMonitorWireSnapshot.CurrentVersion,
                        breakdownWire.CapturedAt,
                        breakdownWire),
                    cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileResourceFactsSubscription(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        var request = ParseResourceFactsRequest(subscription.Query);
        if (request.MetricIds.Count == 0 && request.PublicationDatasetIds.Count == 0)
        {
            return subscription.Compile(async (writer, cancellationToken) =>
                await writer.WriteAsync(subscription.Id, ResourceFactsSnapshot.Empty, cancellationToken));
        }
        var source = services.GetRequiredService<IResourceBreakdownPushSource>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var current in source.SubscribeAsync(
                $"resource-facts:{subscription.Id}", request, interval, cancellationToken))
            {
                await writer.WriteAsync(subscription.Id,
                    ResourceFactsSnapshot.Create(current, request.MetricIds, ReadResourceFoundations(request, services)), cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileOperationSubscription(
        ParsedFrontendSubscription subscription,
        IHostManagerOperationQueryService? operations)
    {
        if (operations is null)
        {
            return subscription.Compile(static async (_, cancellationToken) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }

        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var state in operations.SubscribeAsync(cancellationToken))
            {
                await writer.WriteAsync(
                    subscription.Id,
                    HostManagerOperationWireProjection.Project(state),
                    cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileManagementInventoryRevisionSubscription(
        ParsedFrontendSubscription subscription,
        IManagementInventoryRevisionOwner inventoryRevisions)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in inventoryRevisions.SubscribeAsync(cancellationToken))
            {
                await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
            }
        });

    private static CompiledFrontendSubscription CompileAccessRequestSubscription(
        ParsedFrontendSubscription subscription,
        ITrustedAccessRequestOwner requests)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var pending in requests.SubscribePendingAsync(cancellationToken))
            {
                var wire = pending.Select(static request => new TrustedAccessRequestPendingWireSnapshot(
                    request.RequestId,
                    request.Name,
                    request.RequestedScopes.ToArray(),
                    request.ObservedExecutablePath,
                    request.ObservedProcessId,
                    request.SubmittedAt,
                    request.ExpiresAt)).ToArray();
                await writer.WriteAsync(subscription.Id, wire, cancellationToken);
            }
        });

    private static CompiledFrontendSubscription CompileCpuTopologySubscription(
        ParsedFrontendSubscription subscription,
        ICpuTopologyReader topology,
        TimeSpan interval,
        bool fieldsOnly = false)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            if (fieldsOnly)
            {
                await foreach (var fields in topology.SubscribeInformationAsync(
                    $"frontend-channel:{subscription.Id}", interval, cancellationToken))
                    await writer.WriteAsync(subscription.Id, fields, cancellationToken);
                return;
            }
            await foreach (var snapshot in topology.SubscribeAsync(
                $"frontend-channel:{subscription.Id}", interval, cancellationToken))
            {
                await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileCpuResidencySubscription(
        ParsedFrontendSubscription subscription,
        ICpuCoreResidencyReader residency,
        TimeSpan interval)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in residency.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                interval,
                cancellationToken))
            {
                await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileDeviceTopologySubscription(
        ParsedFrontendSubscription subscription,
        IDeviceTopologySnapshotProvider topology,
        TimeSpan interval,
        bool fieldsOnly = false)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in topology.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                interval,
                cancellationToken))
            {
                if (fieldsOnly)
                    await writer.WriteAsync(subscription.Id, snapshot.InformationFields, cancellationToken);
                else
                    await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
            }
        });
    }

    private static CompiledFrontendSubscription CompileAcpiInformationSubscription(
        ParsedFrontendSubscription subscription,
        IDeviceTopologySnapshotProvider topology,
        TimeSpan interval)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in topology.SubscribeAcpiAsync(
                $"frontend-channel:{subscription.Id}", interval, cancellationToken))
                await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
        });
    }

    private static CompiledFrontendSubscription CompileControlActualSubscription(
        ParsedFrontendSubscription subscription,
        IControlActualStateOwner owner,
        TimeSpan interval)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var demand = owner.AcquireSubscription();
            await PublishAlignedPeriodicSubscriptionAsync(subscription.Id, () => owner.Current,
                interval, writer, cancellationToken);
        });

    private static CompiledFrontendSubscription CompileAlignedPeriodicSubscription<T>(
        ParsedFrontendSubscription subscription,
        Func<T> readCurrent,
        TimeSpan interval)
        => subscription.Compile((writer, cancellationToken) =>
            PublishAlignedPeriodicSubscriptionAsync(
                subscription.Id,
                readCurrent,
                interval,
                writer,
                cancellationToken));

    private static CompiledFrontendSubscription CompileAlignedPeriodicAsyncSubscription<T>(
        ParsedFrontendSubscription subscription,
        Func<CancellationToken, Task<T>> readCurrent,
        TimeSpan interval)
        => subscription.Compile((writer, cancellationToken) =>
            PublishAlignedPeriodicAsyncSubscriptionAsync(
                subscription.Id,
                readCurrent,
                interval,
                writer,
                cancellationToken));

    private static async Task PublishAlignedPeriodicAsyncSubscriptionAsync<T>(
        string subscriptionId,
        Func<CancellationToken, Task<T>> readCurrent,
        TimeSpan interval,
        FrontendSubscriptionChannelWriter writer,
        CancellationToken cancellationToken)
    {
        var intervalMilliseconds = Math.Max(
            1L,
            checked((long)Math.Ceiling(interval.TotalMilliseconds)));
        while (!cancellationToken.IsCancellationRequested)
        {
            var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextBoundaryMilliseconds = checked(
                ((nowMilliseconds / intervalMilliseconds) + 1L)
                * intervalMilliseconds);
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    nextBoundaryMilliseconds - nowMilliseconds),
                cancellationToken);
            await writer.WriteAsync(
                subscriptionId,
                await readCurrent(cancellationToken),
                cancellationToken);
        }
    }

    private static async Task PublishAlignedPeriodicSubscriptionAsync<T>(
        string subscriptionId,
        Func<T> readCurrent,
        TimeSpan interval,
        FrontendSubscriptionChannelWriter writer,
        CancellationToken cancellationToken)
    {
        var intervalMilliseconds = Math.Max(
            1L,
            checked((long)Math.Ceiling(interval.TotalMilliseconds)));
        await writer.WriteAsync(
            subscriptionId,
            readCurrent(),
            cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextBoundaryMilliseconds = checked(
                ((nowMilliseconds / intervalMilliseconds) + 1L)
                * intervalMilliseconds);
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    nextBoundaryMilliseconds - nowMilliseconds),
                cancellationToken);
            await writer.WriteAsync(
                subscriptionId,
                readCurrent(),
                cancellationToken);
        }
    }

    internal static bool TryParseSubscriptionSelector(
        string? value,
        out string selector,
        out IQueryCollection query)
    {
        selector = string.Empty;
        query = QueryCollection.Empty;
        if (string.IsNullOrEmpty(value)
            || value.Length > SubscriptionChannelMaximumPathLength
            || value.Any(static character =>
                character == '\\'
                || character == '#'
                || char.IsControl(character)
                || char.IsWhiteSpace(character)))
        {
            return false;
        }

        var separator = value.IndexOf('?');
        selector = separator < 0 ? value : value[..separator];
        // **一级路由**：查路由表。查得到即合法，而且此刻已经确定了是哪条订阅源 ——
        // 这里不再另有一份选择器名单，加一条源只改一处。
        if (!SubscriptionSourceRoutes.ContainsKey(selector))
        {
            return false;
        }

        var rawQuery = separator < 0 ? string.Empty : value[separator..];
        query = rawQuery.Length == 0
            ? QueryCollection.Empty
            : new QueryCollection(QueryHelpers.ParseQuery(rawQuery));
        return true;
    }

    internal static bool IsValidSubscriptionId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > SubscriptionChannelMaximumIdLength)
        {
            return false;
        }
        return value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or ':' or '-' or '_');
    }

    private sealed record ParsedFrontendSubscription(
        string Id,
        string Selector,
        IQueryCollection Query)
    {
        public CompiledFrontendSubscription Compile(
            Func<FrontendSubscriptionChannelWriter, CancellationToken, Task> runAsync)
            => new(Id, Selector, runAsync);
    }

    internal sealed record CompiledFrontendSubscription(
        string Id,
        string Selector,
        Func<FrontendSubscriptionChannelWriter, CancellationToken, Task> RunAsync);

    internal sealed class FrontendSubscriptionChannelWriter(
        HttpResponse response,
        JsonSerializerOptions serializerOptions,
        Func<CancellationToken, ValueTask<bool>>? validateCredential = null,
        Func<string?, object?, CancellationToken, ValueTask>? receiveHook = null) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        internal async ValueTask WriteHookAsync<T>(MonitoringHookFrame<T> frame, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (validateCredential is not null && !await validateCredential(token))
                    throw new UnauthorizedAccessException("The subscription credential is no longer valid.");
                if (receiveHook is not null) await receiveHook(frame.HookId, frame.Value, token);
                else await NdjsonResponse.WriteAsync(response, frame, serializerOptions, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new FrontendSubscriptionChannelWriteException(error); }
            finally { gate.Release(); }
        }

        public async ValueTask WriteAsync<T>(
            string subscriptionId,
            T value,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                // A notification is only a wakeup hint. Each frame checks the owner's
                // committed persistent revision before disclosing another current value.
                if (validateCredential is not null && !await validateCredential(cancellationToken))
                    throw new UnauthorizedAccessException("The subscription credential is no longer valid.");
                await NdjsonResponse.WriteAsync(
                    response,
                    new FrontendSubscriptionChannelFrame<T>(subscriptionId, value),
                    serializerOptions,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FrontendSubscriptionChannelWriteException(exception);
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose() => gate.Dispose();
    }

    private sealed class FrontendSubscriptionChannelWriteException(
        Exception innerException)
        : Exception("The frontend callback transport write failed.", innerException);
}

internal sealed record FrontendSubscriptionChannelRequest(
    int Version,
    IReadOnlyList<FrontendSubscriptionChannelRequestItem?>? Subscriptions,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    JsonElement Hooks = default);

internal sealed record FrontendSubscriptionChannelRequestItem(
    string? Id,
    string? Path);

internal sealed record FrontendSubscriptionChannelFrame<T>(
    string SubscriptionId,
    T Value);

internal sealed record SubscriptionChannelRejection(int StatusCode, string Error,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Position = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

internal sealed record SubscriptionChannelCompilationResult(
    IReadOnlyList<ResourceManager.App.Endpoints.ResourceManagerEndpointRouteBuilderExtensions.CompiledFrontendSubscription> Subscriptions,
    int StatusCode,
    string Error,
    IReadOnlyList<FrontendSubscriptionChannelFrame<SubscriptionChannelRejection>> Rejections,
    IReadOnlyList<MonitoringHookFrame<SubscriptionChannelRejection>>? HookRejections = null,
    IReadOnlyList<MonitoringHookFrame<MonitoringHookExpansion>>? HookExpansions = null)
{
    public bool Succeeded => StatusCode == StatusCodes.Status200OK;
}
