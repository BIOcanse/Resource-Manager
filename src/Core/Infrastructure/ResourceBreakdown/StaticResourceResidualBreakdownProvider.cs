using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class StaticResourceResidualBreakdownProvider : IResourceResidualBreakdownProvider
{
    public IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request)
    {
        if (request.Value <= 0)
        {
            return [];
        }

        return
        [
            new ResourceProcessSegment(
                -1,
                string.Empty,
                null,
                request.Value,
                request.SystemPercent,
                100,
                null,
                null,
                ResourceProcessAttributionKinds.SystemResidual)
        ];
    }

}
