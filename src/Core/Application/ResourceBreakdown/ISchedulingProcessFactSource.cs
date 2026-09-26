using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface ISchedulingProcessFactSource
{
    Task<SchedulingProcessFactSnapshot> CaptureAsync(
        SchedulingProcessFactRequest request,
        CancellationToken cancellationToken);
}
