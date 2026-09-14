using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class ServiceAutoStartSettings(
    RuntimePlanProvider plans,
    BackendHostEnvironment environment,
    ILogger<ServiceAutoStartSettings> logger) : IHostedService
{
    private bool? applied;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        plans.Published += Apply;
        if (plans.Current.Version > 0)
        {
            try { Apply(plans.Current); }
            catch (Exception exception)
            {
                logger.LogError(exception, "The saved autostart preference could not be applied to SCM.");
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        plans.Published -= Apply;
        return Task.CompletedTask;
    }

    private void Apply(CompiledRuntimePlan plan)
    {
        if (applied == plan.AutoStartEnabled) return;
        if (!environment.IsWindowsService)
        {
            if (plan.AutoStartEnabled)
                throw new InvalidOperationException("Autostart requires the installed Windows service; the development backend does not change SCM.");
            return;
        }
        WindowsServiceRegistration.SetAutoStart(WindowsServiceRegistration.ProductServiceName,
            Environment.ProcessPath ?? throw new InvalidOperationException("Missing backend executable path."),
            plan.AutoStartEnabled);
        applied = plan.AutoStartEnabled;
    }
}
