using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed partial class OptimizationProcessPolicyEngine(
    IHostManagerReportService reportService,
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IProcessResourcePolicyWriter policyWriter,
    ILogger logger)
{
}
