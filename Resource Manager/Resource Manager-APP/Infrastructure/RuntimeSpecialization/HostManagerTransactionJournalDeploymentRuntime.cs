using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerTransactionJournalDeploymentRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal HostManagerTransactionJournalRuntimePlan CaptureDesired()
    {
        var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
        var recreate = hostPlan.HostRecreate.TransactionJournal;
        var hotPublish = hostPlan.HotPublish.TransactionJournal;
        if (!recreate.IsPublished || !hotPublish.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager transaction-journal configuration is not published.");
        }

        return new HostManagerTransactionJournalRuntimePlan(
            hostPlan,
            hostPlan.BuildSpecialize.TransactionJournalAbiVersion,
            recreate,
            hotPublish);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.TransactionJournal,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.TransactionJournal,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.TransactionJournal,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.TransactionJournal, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => deploymentState.CompleteAttemptSucceeded(token);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult)
        => deploymentState.CompleteAttemptFailed(token, failureCode, nativeResult);
}

internal sealed record HostManagerTransactionJournalRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerTransactionJournalRecreatePlan Recreate,
    CompiledHostManagerTransactionJournalHotPublishPlan HotPublish)
{
    internal bool HasSameModuleConfiguration(
        HostManagerTransactionJournalRuntimePlan other)
        => AbiVersion == other.AbiVersion
            && Recreate == other.Recreate
            && HotPublish == other.HotPublish;
}
