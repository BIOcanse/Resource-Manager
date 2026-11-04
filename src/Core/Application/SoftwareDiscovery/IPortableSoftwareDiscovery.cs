namespace ResourceManager.App.Application.SoftwareDiscovery;

public interface IPortableSoftwareDiscovery
{
    Task ScanRunningProcessesAsync(CancellationToken cancellationToken);
}
