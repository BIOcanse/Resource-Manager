using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.RuntimeSpecialization;

public interface IRuntimeSpecializationCoordinator
{
    Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(
        double? overrideRatio,
        CancellationToken cancellationToken);

    Task<RuntimePlanPublicationResult> RebuildAsync(
        string reason,
        CancellationToken cancellationToken);

    Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(
        AppSettings settings,
        string reason,
        CancellationToken cancellationToken);

    Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(
        AppSettingsPatchRequest patch,
        string reason,
        CancellationToken cancellationToken);
}
