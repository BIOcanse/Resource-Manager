using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class RuntimeSpecializationCoordinator
{
    public async Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(
        double? overrideRatio,
        CancellationToken cancellationToken)
    {
        if (overrideRatio.HasValue) CpuBaselineRatio.Validate(overrideRatio.Value);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (overrideRatio.HasValue)
            {
                cpuOverrides.SaveBaselineRatio(overrideRatio.Value);
            }
            else
            {
                cpuOverrides.ResetBaselineRatio();
            }

            // Once persisted, finish publication even if the HTTP caller disconnects.
            try
            {
                var publication = await CompileAndPublishAsync("cpu-baseline-ratio-changed", CancellationToken.None);
                return new CpuBaselineRatioUpdateResult(publication.Plan.CpuBaseline, overrideRatio,
                    publication.HasDeliveryFailures ? "appliedWithDeliveryFailures" : "applied");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "CPU baseline ratio saved, but runtime plan publication failed.");
                return new CpuBaselineRatioUpdateResult(null, overrideRatio, "savedNotApplied");
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
