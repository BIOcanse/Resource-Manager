using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerSmartCoordinatorRuntime(
    HostManagerDeploymentState deploymentState)
{
    internal HostManagerSmartCoordinatorRuntimePlan CaptureDesired(
        RuntimePlanPublicationLease publicationLease)
    {
        ArgumentNullException.ThrowIfNull(publicationLease);
        var runtimePlan = publicationLease.Plan;
        var hostPlan = runtimePlan.HostManager.RequirePublished();
        var smartCoordinator = hostPlan.SmartCoordinator;
        if (!smartCoordinator.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager smart coordinator configuration is not published.");
        }

        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(
            smartCoordinator.Build,
            smartCoordinator.Recreate,
            smartCoordinator.HotPublish,
            smartCoordinator.ConfigurationGeneration);
        var digest = NativeSmartCoordinatorConfigurationWriter.ComputeSha256(
            in configuration,
            checked((uint)smartCoordinator.HotPublish.MaximumActionsPerRealtimeTick));
        if (!string.Equals(digest, smartCoordinator.ConfigurationSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Host Manager smart coordinator native configuration identity does not match the compiled plan.");
        }

        return new HostManagerSmartCoordinatorRuntimePlan(
            runtimePlan,
            publicationLease.PublicationSequence,
            hostPlan,
            smartCoordinator,
            configuration);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SmartCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SmartCoordinator,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SmartCoordinator,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    public bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.SmartCoordinator, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => deploymentState.CompleteAttemptSucceeded(token);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult)
        => deploymentState.CompleteAttemptFailed(
            token,
            failureCode,
            nativeResult);
}

internal sealed record HostManagerSmartCoordinatorRuntimePlan(
    CompiledRuntimePlan RuntimePlan,
    ulong PublicationSequence,
    CompiledHostManagerPlan HostPlan,
    CompiledHostManagerSmartCoordinatorPlan SmartCoordinator,
    NativeSmartCoordinatorConfiguration Configuration);
