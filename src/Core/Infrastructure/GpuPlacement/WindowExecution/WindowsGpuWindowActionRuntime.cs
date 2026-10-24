namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

public sealed class WindowsGpuWindowActionRuntime
{
    private readonly string executable;
    private readonly string? desktop;
    private readonly ILogger? logger;

    public WindowsGpuWindowActionRuntime(ILogger<WindowsGpuWindowActionRuntime>? logger = null)
        : this(Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", "ResourceManager.GpuWindowAction.exe"), null, logger) { }

    internal WindowsGpuWindowActionRuntime(string executable, string? desktop, ILogger? logger = null)
    {
        this.executable = Path.GetFullPath(executable);
        this.desktop = desktop;
        this.logger = logger;
    }

    internal WindowsGpuWindowActionExecutor Create(GpuWindowActionProcessLimits limits, CancellationToken cancellationToken, ulong deadlineMilliseconds)
        => new(executable, limits, cancellationToken, desktop, deadlineMilliseconds, logger);
}
