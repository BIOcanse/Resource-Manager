using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.Adaptation;
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

    private static async Task RunSubscriptionChannelWorkerAsync(
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
                "Frontend logical callback ended for {SubscriptionId} ({Selector}) on {TraceIdentifier}; the item will remain silent.",
                subscription.Id,
                subscription.Selector,
                traceIdentifier);
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

    private static CompiledFrontendSubscription CompileFrontendSubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services)
    {
        var query = subscription.Query;
        var interval = ParseSamplingSubscriptionInterval(query);
        return subscription.Selector switch
        {
            "/api/metrics/subscribe" => CompileMetricSubscription(
                subscription,
                services,
                interval),
            "/api/metrics/gpu-specialized/subscribe" => CompileGpuSubscription(
                subscription,
                services,
                interval),
            "/api/resource-monitor/subscribe" => CompileResourceSubscription(
                subscription,
                services,
                interval),
            "/api/adapters/resource-manager/scheduling/subscribe" =>
                CompileAlignedPeriodicSubscription(
                    subscription,
                    services.GetRequiredService<
                        IResourceManagerSelfSchedulingControl>()
                        .GetSchedulingSnapshot,
                    interval),
            "/api/local-system/status/subscribe" =>
                CompileAlignedPeriodicSubscription(
                    subscription,
                    services.GetRequiredService<ILocalSystemStatusProvider>()
                        .GetStatus,
                    interval),
            "/api/device-topology/state/subscribe" =>
                CompileDeviceTopologySubscription(
                    subscription,
                    services.GetRequiredService<IDeviceTopologySnapshotProvider>(),
                    interval),
            "/api/cpu/topology/subscribe" =>
                CompileCpuTopologySubscription(
                    subscription,
                    services.GetRequiredService<ICpuTopologyReader>(),
                    interval),
            "/api/cpu/residency/subscribe" =>
                CompileCpuResidencySubscription(
                    subscription,
                    services.GetRequiredService<ICpuCoreResidencyReader>(),
                    interval),
            "/api/optimization/smart/state/subscribe" =>
                CompileAlignedPeriodicAsyncSubscription(
                    subscription,
                    services.GetRequiredService<IHostManagerSmartCoordinator>()
                        .GetStateAsync,
                    interval),
            "/api/operations/subscribe" => CompileOperationSubscription(
                subscription,
                services.GetService<IHostManagerOperationQueryService>()),
            _ => throw new InvalidOperationException(
                $"Unsupported subscription selector '{subscription.Selector}'.")
        };
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
        if (selector is not (
            "/api/metrics/subscribe"
            or "/api/metrics/gpu-specialized/subscribe"
            or "/api/resource-monitor/subscribe"
            or "/api/adapters/resource-manager/scheduling/subscribe"
            or "/api/local-system/status/subscribe"
            or "/api/device-topology/state/subscribe"
            or "/api/cpu/topology/subscribe"
            or "/api/cpu/residency/subscribe"
            or "/api/optimization/smart/state/subscribe"
            or "/api/operations/subscribe"))
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

    private sealed record CompiledFrontendSubscription(
        string Id,
        string Selector,
        Func<FrontendSubscriptionChannelWriter, CancellationToken, Task> RunAsync);

    private sealed class FrontendSubscriptionChannelWriter(
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
