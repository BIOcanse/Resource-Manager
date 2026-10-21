using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

internal sealed class HostManagerSamplingSubscriptionTestFixture : IDisposable
{
    private readonly HostManagerDeploymentState deployment = new();
    private readonly RuntimePlanProvider provider;

    public HostManagerSamplingSubscriptionTestFixture(
        CompiledHostManagerPlan? hostManagerPlan = null,
        ResourceManagerLedgerOwner? ledgerOwner = null)
    {
        provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostManagerPlan ?? HostManagerTestPlanFactory.CreatePlan()
        });
        Owner = new HostManagerSamplingSubscriptionOwner(
            provider,
            new HostManagerSamplingSubscriptionRuntime(provider, deployment),
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance, ledgerOwner);
        Owner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public HostManagerSamplingSubscriptionOwner Owner { get; }

    internal HostManagerDeploymentState Deployment => deployment;

    public RuntimePlanProvider Provider => provider;

    public void Dispose()
    {
        Owner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Owner.Dispose();
    }
}
