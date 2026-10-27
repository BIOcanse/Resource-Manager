using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record MonitoringHookEvent(string State, MonitoringHookTarget Target,
    string Quantity, double? Reading, string Unit, double Threshold,
    DateTimeOffset? Since, string? Reason, IReadOnlyList<MonitoringHookOwner> Owners);
internal sealed record MonitoringHookOwner(string Name, int ProcessId, double Reading);
internal sealed record MonitoringHookFrame<T>(string? HookId, T Value);
internal sealed record MonitoringHookExpansion(string State, ExpandedMonitoringHook Hook);
internal sealed record MonitoringHookWaitResult(string? HookId, ExpandedMonitoringHook Hook, MonitoringHookEvent Value);
