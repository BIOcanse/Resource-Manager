using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

internal sealed class HostManagerSamplingSubscriptionTestFixture : IDisposable
{
    private readonly HostManagerDeploymentState deployment = new();
    private readonly RuntimePlanProvider provider;

    public HostManagerSamplingSubscriptionTestFixture(
        CompiledHostManagerPlan? hostManagerPlan = null)
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
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);
        Owner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public HostManagerSamplingSubscriptionOwner Owner { get; }

    public RuntimePlanProvider Provider => provider;

    public void Dispose()
    {
        Owner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Owner.Dispose();
    }
}
