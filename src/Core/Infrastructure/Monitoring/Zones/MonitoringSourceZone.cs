using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public class MonitoringSourceZone : IMonitoringSourceZone
{
    public MonitoringSourceZone(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        DisplayName = sourceId;
    }

    public string DisplayName { get; }
}
