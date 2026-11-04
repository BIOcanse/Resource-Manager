using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class RuntimePlanProvider(HostManagerDeploymentState hostManagerDeploymentState) : IRuntimePlanProvider
{
    private readonly object publicationGate = new();
    private int activePublicationLeases;

    internal event Action<CompiledRuntimePlan>? Published;

    public CompiledRuntimePlan Current => hostManagerDeploymentState.CurrentRuntimePlan;

    /// <summary>Waits for the first real input publication without starting or sampling an owner.</summary>
    internal async Task<CompiledRuntimePlan> WaitForPublishedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is { HostManager.IsPublished: true } current) return current;
        var published = new TaskCompletionSource<CompiledRuntimePlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(CompiledRuntimePlan plan)
        { if (plan.HostManager.IsPublished) published.TrySetResult(plan); }
        Published += Changed;
        try
        {
            Changed(Current);
            return await published.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Published -= Changed; }
    }

    // Capture one immutable publication; reading these facts never starts work.
    internal RuntimePlanDecisionFacts DecisionFacts
    {
        get
        {
            var plan = Current;
            return new(
                plan.Diagnostics.DebugLogEnabled,
                plan.AutomaticUpdatesEnabled,
                plan.OptimizationMode.SchedulingEnabled,
                GpuApiHistoryEnabled: true,
                BackgroundReportsEnabled: !plan.HostManager.ReportCoordinator.Recreate.Rules.IsDefaultOrEmpty,
                PortableDiscoveryEnabled: true,
                KernelLeakObservationEnabled: plan.HostManager.KernelLeak?.Enabled == true,
                plan.HostManager.PublicServiceCoordinator.HotPublish.ServiceEnabled);
        }
    }

    public RuntimePlanPublicationLease AcquirePublicationLease()
    {
        lock (publicationGate)
        {
            var publication =
                hostManagerDeploymentState.CaptureRuntimePlanPublication();
            activePublicationLeases = checked(activePublicationLeases + 1);
            return RuntimePlanPublicationLease.CreateTracked(
                publication.Plan,
                publication.PublicationSequence,
                ReleasePublicationLease);
        }
    }

    public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HostManagerRuntimePlanPublication publication;
        lock (publicationGate)
        {
            while (activePublicationLeases != 0)
            {
                Monitor.Wait(publicationGate);
            }

            FreedomPointPlanApplication.ValidatePublication(Current, plan);
            hostManagerDeploymentState.PublishRuntimePlan(plan);
            publication =
                hostManagerDeploymentState.CaptureRuntimePlanPublication();
        }

        var failures = new List<RuntimePlanDeliveryFailure>();
        if (Published is { } published)
        {
            foreach (var subscriber in published.GetInvocationList())
            {
                try
                {
                    ((Action<CompiledRuntimePlan>)subscriber)(plan);
                }
                catch (Exception exception)
                {
                    failures.Add(new RuntimePlanDeliveryFailure(
                        $"{subscriber.Method.DeclaringType?.FullName ?? "<unknown>"}.{subscriber.Method.Name}",
                        exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message));
                }
            }
        }

        return new RuntimePlanPublicationResult(
            publication.Plan,
            publication.PublicationSequence,
            failures);
    }

    private void ReleasePublicationLease()
    {
        lock (publicationGate)
        {
            if (activePublicationLeases <= 0)
            {
                throw new InvalidOperationException(
                    "The runtime plan publication lease count is invalid.");
            }

            activePublicationLeases--;
            if (activePublicationLeases == 0)
            {
                Monitor.PulseAll(publicationGate);
            }
        }
    }
}

internal readonly record struct RuntimePlanDecisionFacts(
    bool DebugLoggingEnabled,
    bool AutomaticUpdatesEnabled,
    bool OptimizationEnabled,
    bool GpuApiHistoryEnabled,
    bool BackgroundReportsEnabled,
    bool PortableDiscoveryEnabled,
    bool KernelLeakObservationEnabled,
    bool PublicServiceEnabled);
