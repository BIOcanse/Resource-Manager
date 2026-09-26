using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

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
            if (!TryCompileSubscriptionChannelRequest(
                request,
                httpContext.RequestServices,
                out var subscriptions,
                out var statusCode,
                out var error))
            {
                httpContext.Response.StatusCode = statusCode;
                await httpContext.Response.WriteAsJsonAsync(
                    new { error },
                    jsonOptions.Value.SerializerOptions,
                    cancellationToken);
                return;
            }

            NdjsonResponse.Prepare(httpContext.Response);
            using var channelCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var writer = new FrontendSubscriptionChannelWriter(
                httpContext.Response,
                jsonOptions.Value.SerializerOptions);
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

        return app;
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

    private static bool TryCompileSubscriptionChannelRequest(
        FrontendSubscriptionChannelRequest? request,
        IServiceProvider services,
        out IReadOnlyList<CompiledFrontendSubscription> subscriptions,
        out int statusCode,
        out string error)
    {
        subscriptions = [];
        statusCode = StatusCodes.Status400BadRequest;
        error = string.Empty;
        if (request is null || request.Version != 1)
        {
            error = "Subscription channel version 1 is required.";
            return false;
        }
        if (request.Subscriptions is null
            || request.Subscriptions.Count is < 1
                or > SubscriptionChannelMaximumItemCount)
        {
            error = $"The subscription count must be from 1 through {SubscriptionChannelMaximumItemCount}.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new List<ParsedFrontendSubscription>(
            request.Subscriptions.Count);
        foreach (var item in request.Subscriptions)
        {
            if (item is null
                || !IsValidSubscriptionId(item.Id)
                || !ids.Add(item.Id!))
            {
                error = "Every subscription must have a unique valid id.";
                return false;
            }
            if (!TryParseSubscriptionSelector(
                item.Path,
                out var selector,
                out var query))
            {
                error = $"Subscription '{item.Id}' has an unsupported path.";
                return false;
            }
            descriptors.Add(new ParsedFrontendSubscription(
                item.Id!,
                selector,
                query));
        }

        try
        {
            subscriptions = descriptors
                .Select(descriptor => CompileFrontendSubscription(
                    descriptor,
                    services))
                .ToArray();
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or FormatException
                or OverflowException)
        {
            error = "One or more subscription queries are invalid.";
            return false;
        }
        catch (InvalidOperationException)
        {
            statusCode = StatusCodes.Status503ServiceUnavailable;
            error = "One or more subscription sources are unavailable.";
            return false;
        }
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
            ["/api/metrics/subscribe"] = new((subscription, services, interval) =>
                CompileMetricSubscription(subscription, services, interval)),
            ["/api/metrics/gpu-specialized/subscribe"] = new((subscription, services, interval) =>
                CompileGpuSubscription(subscription, services, interval)),
            ["/api/resource-monitor/subscribe"] = new((subscription, services, interval) =>
                CompileResourceSubscription(subscription, services, interval)),
            ["/api/adapters/resource-manager/scheduling/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicSubscription(
                    subscription,
                    services.GetRequiredService<IResourceManagerSelfSchedulingControl>()
                        .GetSchedulingSnapshot,
                    interval)),
            // 控制面的实际状态：这台机器现在实际是什么样。
            // 只返回当前值，采样由后台那条独立的路做 —— 订阅者再多也不会多碰一次硬件。
            ["/api/control/actual/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicSubscription(
                    subscription,
                    () => services.GetRequiredService<IControlActualStateOwner>().Current,
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
            ["/api/cpu/topology/subscribe"] =
                new((subscription, services, interval) => CompileCpuTopologySubscription(
                    subscription,
                    services.GetRequiredService<ICpuTopologyReader>(),
                    interval)),
            ["/api/cpu/residency/subscribe"] =
                new((subscription, services, interval) => CompileCpuResidencySubscription(
                    subscription,
                    services.GetRequiredService<ICpuCoreResidencyReader>(),
                    interval)),
            ["/api/optimization/smart/state/subscribe"] =
                new((subscription, services, interval) => CompileAlignedPeriodicAsyncSubscription(
                    subscription,
                    services.GetRequiredService<IHostManagerSmartCoordinator>().GetStateAsync,
                    interval)),
            // 操作队列不按间隔采样：它是推来的，来一条发一条。
            ["/api/operations/subscribe"] = new((subscription, services, _) =>
                CompileOperationSubscription(
                    subscription,
                    services.GetService<IHostManagerOperationQueryService>()))
        };

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

    private static CompiledFrontendSubscription CompileMetricSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services,
        TimeSpan interval)
    {
        var runtimePlanProvider = services.GetRequiredService<IRuntimePlanProvider>();
        var snapshotRequest = subscription.Query.TryGetValue("ids", out var ids)
            ? MetricSampleRequest.ForIds(ids)
            : runtimePlanProvider.Current.Monitoring.DashboardMetricRequest;
        var source = services.GetRequiredService<IMetricSnapshotPushSource>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in source.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                snapshotRequest,
                interval,
                cancellationToken))
            {
                var presentation = MetricCatalog.ApplyPresentation(snapshot);
                await writer.WriteAsync(
                    subscription.Id,
                    MetricSnapshotWireSnapshot.From(presentation, snapshotRequest),
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
        var monitoringPlan = services.GetRequiredService<IRuntimePlanProvider>()
            .Current.Monitoring;
        var scope = ParseResourceMonitorScope(subscription.Query);
        var metricIds = scope == ResourceMonitorScope.Table
            ? []
            : ParseResourceMetricIds(subscription.Query, monitoringPlan);
        var requestedSampleMetricIds = ParseResourceSampleMetricIds(
            subscription.Query);
        var scaleModes = ParseResourceScaleModes(
            subscription.Query,
            monitoringPlan);
        var tableRequest = ParseResourceTableRequest(
            subscription.Query,
            monitoringPlan);
        var processDetailSoftwareIds =
            ParseResourceBreakdownProcessDetailSoftwareIds(subscription.Query);
        var sampleMetricIds = AddTableRequiredMetricIds(
            requestedSampleMetricIds.Count > 0
                ? requestedSampleMetricIds
                : metricIds,
            scope == ResourceMonitorScope.Bars ? [] : tableRequest.ColumnIds);
        var sampleRequest = new ResourceBreakdownSampleRequest(
            sampleMetricIds,
            scaleModes,
            ResolveProcessSampleDetailLevel(subscription.Query, tableRequest));
        var source = services.GetRequiredService<IResourceBreakdownPushSource>();
        var tableProjector = services.GetRequiredService<IResourceTableProjector>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var sampledBreakdown in source.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                sampleRequest,
                interval,
                cancellationToken))
            {
                var table = tableProjector.Project(sampledBreakdown, tableRequest);
                var breakdownWire = ResourceBreakdownWireSnapshot.Create(
                    sampledBreakdown,
                    metricIds,
                    processDetailSoftwareIds);
                await writer.WriteAsync(
                    subscription.Id,
                    new ResourceMonitorWireSnapshot(
                        ResourceMonitorWireSnapshot.CurrentVersion,
                        MaxTimestamp(breakdownWire.CapturedAt, table.CapturedAt),
                        breakdownWire,
                        ResourceTableWireSnapshot.Create(table)),
                    cancellationToken);
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

    private static CompiledFrontendSubscription CompileCpuTopologySubscription(
        ParsedFrontendSubscription subscription,
        ICpuTopologyReader topology,
        TimeSpan interval)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
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
        TimeSpan interval)
    {
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var snapshot in topology.SubscribeAsync(
                $"frontend-channel:{subscription.Id}",
                interval,
                cancellationToken))
            {
                await writer.WriteAsync(subscription.Id, snapshot, cancellationToken);
            }
        });
    }

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
        JsonSerializerOptions serializerOptions) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        public async ValueTask WriteAsync<T>(
            string subscriptionId,
            T value,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
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
    IReadOnlyList<FrontendSubscriptionChannelRequestItem?>? Subscriptions);

internal sealed record FrontendSubscriptionChannelRequestItem(
    string? Id,
    string? Path);

internal sealed record FrontendSubscriptionChannelFrame<T>(
    string SubscriptionId,
    T Value);
