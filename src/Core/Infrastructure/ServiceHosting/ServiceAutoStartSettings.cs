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
    private readonly object gate = new();
    private readonly Action<bool> setAutoStart = static enabled =>
        WindowsServiceRegistration.SetAutoStart(WindowsServiceRegistration.ProductServiceName,
            Environment.ProcessPath ?? throw new InvalidOperationException("Missing backend executable path."), enabled);
    private Action<CompiledRuntimePlan>? onPublished;
    private bool? applied;

    internal ServiceAutoStartSettings(RuntimePlanProvider plans, BackendHostEnvironment environment,
        ILogger<ServiceAutoStartSettings> logger, Action<bool> setAutoStart) : this(plans, environment, logger)
    {
        this.setAutoStart = setAutoStart;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (onPublished is not null) return Task.CompletedTask;
            Action<CompiledRuntimePlan> callback = null!;
            callback = plan => Apply(plan, callback);
            onPublished = callback;
            plans.Published += callback;
            if (plans.Current.Version > 0)
            {
                try { callback(plans.Current); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "The saved autostart preference could not be applied to SCM.");
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (onPublished is { } callback) plans.Published -= callback;
            onPublished = null;
        }
        return Task.CompletedTask;
    }

    private void Apply(CompiledRuntimePlan plan, Action<CompiledRuntimePlan> callback)
    {
        lock (gate)
        {
            if (!ReferenceEquals(onPublished, callback)) return;
            if (applied == plan.AutoStartEnabled) return;
            if (!environment.IsWindowsService)
            {
                if (plan.AutoStartEnabled)
                    throw new InvalidOperationException("Autostart requires the installed Windows service; the development backend does not change SCM.");
                return;
            }
            setAutoStart(plan.AutoStartEnabled);
            applied = plan.AutoStartEnabled;
        }
    }
}
