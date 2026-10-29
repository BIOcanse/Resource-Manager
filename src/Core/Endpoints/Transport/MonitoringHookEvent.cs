using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record MonitoringHookEvent(string State, MonitoringHookTarget Target,
    string Quantity, double? Reading, string Unit, double Threshold,
    DateTimeOffset? Since, string? Reason, IReadOnlyList<MonitoringHookOwner> Owners);
internal sealed record MonitoringHookOwner(string Name, int ProcessId, double Reading);
internal sealed record MonitoringHookFrame<T>(string? HookId, T Value);
internal sealed record MonitoringHookExpansion(string State, ExpandedMonitoringHook Hook);
internal sealed record MonitoringHookWaitResult(string? HookId, ExpandedMonitoringHook Hook, object Value);
// The status is the channel's sampled status frame: records and the normal running point have their own frames,
// and an action event already carries the record it appended.
internal sealed record MonitoringEventHookEvent(string State,
    ResourceManager.App.Application.MemoryProtection.HostMemoryProtectionEvent Event,
    ResourceManager.App.Application.MemoryProtection.HostMemoryProtectionLiveStatus Status);
