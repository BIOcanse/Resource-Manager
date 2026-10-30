using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private sealed record MonitoringHookRequest(string Id, MonitoringHookRule? Rule, TimeSpan Interval,
        ExpandedMonitoringHook? Expansion = null, string? Event = null)
    {
        internal string AuthorizationSelector => Event is not null ? "/api/memory-protection/subscribe"
            : MonitoringHookAuthorizationSelector(Rule!.Target.Binding, Rule.TopOwners);
    }

    private static string MonitoringHookAuthorizationSelector(string binding, int topOwners)
        => binding is "software" or "process" || topOwners > 0 ? "/api/resource-monitor/subscribe" : "/api/metrics/subscribe";

    private static async Task<IReadOnlyList<MonitoringHookRequest>> DecodeMonitoringHooksAsync(JsonElement hooks,
        List<MonitoringHookFrame<SubscriptionChannelRejection>> rejections, IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var result = new List<MonitoringHookRequest>();
        if (hooks.ValueKind == JsonValueKind.Undefined) return result;
        if (hooks.ValueKind != JsonValueKind.Array || hooks.GetArrayLength() is < 1 or > SubscriptionChannelMaximumItemCount)
        { Reject(null, "hooks must be an array of 1 through 64 hooks."); return result; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in hooks.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var rawId)
                && rawId.ValueKind == JsonValueKind.String ? rawId.GetString() : null;
            try
            {
                CheckHookObject(item, ["id", "rule", "intervalMs", "condition", "event"]);
                if (!IsValidSubscriptionId(id) || !ids.Add(id!)) throw InvalidHook("hook id must be valid and unique");
                if (item.TryGetProperty("condition", out var condition))
                {
                    if (condition.ValueKind != JsonValueKind.String || item.TryGetProperty("rule", out _) || item.TryGetProperty("intervalMs", out _) || item.TryGetProperty("event", out _))
                        throw InvalidHook("condition must be a string and cannot be mixed with rule or intervalMs");
                    var expanded = await ExpandMonitoringConditionAsync(condition.GetString()!, id!, services, cancellationToken);
                    result.Add(new(id!, expanded.Event is null ? DecodeHookRule(JsonSerializer.SerializeToElement(expanded.Rule, ConditionJsonOptions)) : null,
                        TimeSpan.FromMilliseconds(expanded.IntervalMs), expanded, expanded.Event));
                    continue;
                }
                if (item.TryGetProperty("event", out var eventValue))
                {
                    if (item.TryGetProperty("rule", out _) || item.TryGetProperty("intervalMs", out _)
                        || eventValue.ValueKind != JsonValueKind.String) throw InvalidHook("event cannot be mixed with rule or intervalMs");
                    var expanded = await ExpandMonitoringConditionAsync(eventValue.GetString()!.Replace("memory-protection.", "memory-protection ", StringComparison.Ordinal),
                        id!, services, cancellationToken);
                    if (expanded.Event != eventValue.GetString()) throw InvalidHook("event");
                    result.Add(new(id!, null, TimeSpan.Zero, expanded, expanded.Event));
                    continue;
                }
                if (!item.TryGetProperty("intervalMs", out var interval) || interval.ValueKind != JsonValueKind.Number
                    || !interval.TryGetInt32(out var milliseconds) || milliseconds <= 0) throw InvalidHook("intervalMs must be positive");
                result.Add(new(id!, DecodeHookRule(item.GetProperty("rule")), TimeSpan.FromMilliseconds(milliseconds)));
            }
            catch (MonitoringConditionParseException error)
            { rejections.Add(new(id, new(400, error.Message, error.Position, error.Reason))); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { Reject(id, error.Message); }
        }
        return result;
        void Reject(string? id, string error) => rejections.Add(new(id, new(400, error)));
    }

    private static MonitoringHookRule DecodeHookRule(JsonElement raw)
    {
        CheckHookObject(raw, ["binding", "metricId", "softwareId", "processId", "startKey", "deviceIdentity",
            "quantity", "comparison", "threshold", "holdSeconds", "topOwners"]);
        var binding = Text("binding");
        if (binding is not ("machine" or "software" or "process" or "device")) throw InvalidHook("binding");
        var quantity = Text("quantity");
        if (quantity is not ("value" or "remaining" or "percentOfTotal")) throw InvalidHook("quantity");
        var comparison = Text("comparison");
        if (comparison is not ("below" or "atOrAbove")) throw InvalidHook("comparison");
        var threshold = Number("threshold");
        var seconds = Number("holdSeconds");
        if (seconds <= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) throw InvalidHook("holdSeconds must be positive");
        var hold = TimeSpan.FromSeconds(seconds);
        if (hold <= TimeSpan.Zero) throw InvalidHook("holdSeconds must be at least one clock tick");
        var top = raw.TryGetProperty("topOwners", out _) ? Integer("topOwners") : 0;
        if (top < 0 || top > 0 && binding is "process" or "device") throw InvalidHook("topOwners is meaningful only for machine or software");
        string? software = binding == "software" ? Text("softwareId") : null;
        int? pid = binding == "process" ? Integer("processId") : null;
        var startKey = binding == "process" ? Text("startKey") : null;
        if (binding == "process")
        {
            if (pid <= 0 || !ulong.TryParse(startKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key == 0)
                throw InvalidHook("processId or startKey");
            startKey = key.ToString(CultureInfo.InvariantCulture);
        }
        var device = binding == "device" ? Text("deviceIdentity") : null;
        foreach (var name in new[] { "softwareId", "processId", "startKey", "deviceIdentity" })
            if (raw.TryGetProperty(name, out _) && (name switch
                { "softwareId" => binding != "software", "deviceIdentity" => binding != "device", _ => binding != "process" }))
                throw InvalidHook($"{name} is not applicable to {binding}");
        if (binding is "process" or "software" && quantity != "value") throw InvalidHook("the bound object has no total; select value");
        return new(new(binding, Text("metricId"), software, pid, startKey, device), quantity, comparison, threshold, hold, top);

        string Text(string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) && text == text.Trim()
                ? text : throw InvalidHook(name);
        double Number(string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw InvalidHook(name);
        int Integer(string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : throw InvalidHook(name);
    }

    private static void CheckHookObject(JsonElement item, string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidHook("expected an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw InvalidHook($"unknown or repeated property {property.Name}");
    }
    private static ArgumentException InvalidHook(string detail) => new($"Invalid hook: {detail}.");
    private static bool NumericHookKind(string kind) => kind is "float64" or "signed64" or "unsigned64" or "unsigned128";

    private static CompiledFrontendSubscription CompileMonitoringHook(MonitoringHookRequest request, IServiceProvider services)
    {
        if (request.Event is { } eventName)
            return HookSubscription(request.Id, async (writer, token) =>
            {
                var owner = services.GetRequiredService<ResourceManager.App.Infrastructure.MemoryProtection.HostMemoryProtectionService>();
                await foreach (var publication in owner.SubscribeAsync(token))
                {
                    HostMemoryProtectionLiveStatus? status = null;
                    foreach (var change in publication.Events.Where(change => "memory-protection." + change.Kind == eventName))
                        await writer.WriteHookAsync(new MonitoringHookFrame<MonitoringEventHookEvent>(request.Id,
                            new("met", change, status ??= HostMemoryProtectionLiveStatus.From(publication.Status))), token);
                }
            });
        var rule = request.Rule!;
        var target = rule.Target;
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var hook = new MonitoringHook(request.Id, rule, clock);
        // The hook is the holder passed to the original subscription source. It
        // receives every delivery; no source frame is forwarded to the HTTP caller.
        // Process or software bindings need process data to evaluate the condition.
        var owner = $"monitoring-hook:{request.Id}";
        if (target.Binding is "process" or "software")
        {
            var field = ProcessMetricFieldRegistry.Find(target.MetricId);
            if (field is null || !NumericHookKind(field.ValueKind)) throw InvalidHook("unknown or unsupported process metricId");
            if (field.Code == ProcessMetricFieldCode.CpuResidency
                || field.Code == ProcessMetricFieldCode.GpuResidency && field.GpuEngine is null)
                throw InvalidHook("this process field needs a logical processor or GPU engine scope; select a scalar field");
            var source = services.GetRequiredService<IResourceBreakdownPushSource>();
            var sample = new ResourceBreakdownSampleRequest([field.Name], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
            { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] };
            return HookSubscription(request.Id, async (writer, token) =>
            {
                await foreach (var current in source.SubscribeAsync(owner, sample, request.Interval, token))
                {
                    var reading = ReadHookObject(current, ReadResourceFoundations(sample, services), target, field);
                    if (await hook.DeliverAsync(reading, writer, token)) break;
                }
            });
        }

        var catalogState = services.GetRequiredService<HardwareMetricCatalogState>();
        var catalog = catalogState.Current ?? throw InvalidHook("metric catalogue is unavailable");
        var definition = MetricCatalog.FromSnapshot(catalog).FirstOrDefault(item => item.Id == target.MetricId);
        var fieldBinding = catalog.FieldBindings.Values.FirstOrDefault(item => item.DeviceIdentity == target.DeviceIdentity && item.Field.Name == target.MetricId);
        var address = definition?.Id ?? fieldBinding?.RequestAddress ?? throw InvalidHook("unknown metricId or field address");
        var kind = definition?.ValueKind ?? fieldBinding!.Field.ValueKind;
        var code = definition?.FieldCode ?? fieldBinding!.Field.Code;
        var unit = definition?.Unit ?? fieldBinding!.Field.Unit;
        if (!NumericHookKind(kind)) throw InvalidHook("the selected value is not numeric");
        if (target.Binding == "device" && target.DeviceIdentity != (definition?.DeviceIdentity ?? definition?.ScopeKey ?? fieldBinding?.DeviceIdentity))
            throw InvalidHook("deviceIdentity does not own this metric");
        if (rule.Quantity != "value" && code is not (MetricFieldCode.MemoryUsed or MetricFieldCode.CommitUsed or MetricFieldCode.GpuMemoryUsed))
            throw InvalidHook("this value has no total");
        // The condition reads only the machine metric. A machine hook with top N
        // takes one resource-breakdown sample when it becomes met, then releases it.
        Func<CancellationToken, Task<IReadOnlyList<MonitoringHookOwner>>>? ownersWhenMet = null;
        if (rule.TopOwners > 0)
        {
            var metric = code switch
            {
                MetricFieldCode.MemoryUsed => "memory.usage", MetricFieldCode.CommitUsed => "virtualMemory.usage", MetricFieldCode.CpuUsage => "cpu.usage",
                MetricFieldCode.DiskReadRate => "disk.read", MetricFieldCode.DiskWriteRate => "disk.write",
                MetricFieldCode.NetworkReceiveRate => "network.receive", MetricFieldCode.NetworkSendRate => "network.send",
                _ => ResourceBreakdownMetricIds.IsSupported(target.MetricId) ? target.MetricId : null
            } ?? throw InvalidHook("the selected metric has no resource breakdown");
            var resources = services.GetRequiredService<IResourceBreakdownPushSource>();
            var sample = new ResourceBreakdownSampleRequest([metric], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic);
            ownersWhenMet = token => ReadHookMachineOwnersOnceAsync(resources, $"{owner}:top-owners", sample, metric, token);
        }
        var metrics = services.GetRequiredService<IMetricSnapshotPushSource>();
        return HookSubscription(request.Id, async (writer, token) =>
        {
            await foreach (var current in metrics.SubscribeAsync(owner, MetricSampleRequest.ForIds([address]), request.Interval, token))
                if (await hook.DeliverAsync(ReadHookMetric(current, address, target, catalogState.Catalog!, unit), writer, token, ownersWhenMet)) break;
        });
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Machine monitoring hook condition met")]
    private static async Task<IReadOnlyList<MonitoringHookOwner>> ReadHookMachineOwnersOnceAsync(IResourceBreakdownPushSource resources,
        string holder, ResourceBreakdownSampleRequest sample, string metric, CancellationToken token)
        => await resources.FetchOnceAsync(holder, sample, token) is { } current ? ReadHookMachineOwners(current, metric).Owners : [];

    private static CompiledFrontendSubscription HookSubscription(string id,
        Func<FrontendSubscriptionChannelWriter, CancellationToken, Task> run)
        => new(id, "monitoring-hook", async (writer, token) =>
        {
            try { await run(writer, token); }
            // The original source validates its current sampling-role minimum at
            // admission. A rejected period belongs to this hook, not its channel.
            catch (ArgumentException error)
            { await writer.WriteHookAsync(new MonitoringHookFrame<SubscriptionChannelRejection>(id, new(400, error.Message)), token); }
        });

    private sealed class MonitoringHook(string id, MonitoringHookRule rule, TimeProvider clock)
    {
        private readonly MonitoringHookState state = new(rule, clock);
        internal async ValueTask<bool> DeliverAsync(HookReading current, FrontendSubscriptionChannelWriter writer, CancellationToken token,
            Func<CancellationToken, Task<IReadOnlyList<MonitoringHookOwner>>>? ownersWhenMet = null)
        {
            if (current.Ended) { await WriteAsync("objectEnded", null, null, current.Reason, []); return true; }
            var number = current.Value;
            if (rule.Quantity == "remaining") number = current.Total is { } total && double.IsFinite(total) ? total - number : null;
            if (rule.Quantity == "percentOfTotal") number = current.Total is > 0 && double.IsFinite(current.Total.Value) ? number * 100 / current.Total.Value : null;
            var change = state.Evaluate(number);
            if (change is null) return false;
            IReadOnlyList<MonitoringHookOwner> metOwners = change.Value.State != "met" ? []
                : ownersWhenMet is null ? current.Owners : await ownersWhenMet(token);
            await WriteAsync(change.Value.State, number, change.Value.Since,
                number is null || !double.IsFinite(number.Value) ? current.Reason ?? "value-unavailable" : null,
                metOwners.OrderByDescending(owner => owner.Reading).ThenBy(owner => owner.ProcessId).Take(rule.TopOwners).ToArray());
            return false;

            ValueTask WriteAsync(string status, double? value, DateTimeOffset? since, string? reason, IReadOnlyList<MonitoringHookOwner> owners)
                => writer.WriteHookAsync(new MonitoringHookFrame<MonitoringHookEvent>(id, new(status, rule.Target, rule.Quantity,
                    value is { } n && double.IsFinite(n) ? n : null, rule.Quantity == "percentOfTotal" ? "%" : current.Unit,
                    rule.Threshold, since, reason, owners)), token);
        }
    }
}
