namespace ResourceManager.App.Application.Monitoring;

internal sealed record MonitoringHookTarget(string Binding, string MetricId,
    string? SoftwareId, int? ProcessId, string? StartKey, string? DeviceIdentity);

internal sealed record MonitoringHookRule(MonitoringHookTarget Target, string Quantity,
    string Comparison, double Threshold, TimeSpan Hold, int TopOwners);

// A hook has only state. Its subscription owns delivery, sampling and lifetime.
internal sealed class MonitoringHookState(MonitoringHookRule rule, TimeProvider clock)
{
    private DateTimeOffset? since;
    private long pendingSince;
    private bool met;
    private bool unavailable;

    internal (string State, DateTimeOffset? Since)? Evaluate(double? reading)
    {
        if (reading is null || !double.IsFinite(reading.Value))
        {
            since = null; met = false;
            if (unavailable) return null;
            unavailable = true;
            return ("unavailable", null);
        }
        var wasUnavailable = unavailable;
        unavailable = false;
        var matches = rule.Comparison == "below" ? reading < rule.Threshold : reading >= rule.Threshold;
        if (!matches)
        {
            var changed = wasUnavailable || since is not null;
            var began = since;
            since = null; met = false;
            return changed ? ("cleared", began) : null;
        }
        if (since is null) { since = clock.GetUtcNow(); pendingSince = clock.GetTimestamp(); }
        if (met || clock.GetElapsedTime(pendingSince) < rule.Hold) return null;
        met = true;
        return ("met", since);
    }
}
