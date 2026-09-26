namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

public sealed class WindowsGpuWindowActionRuntime
{
    private readonly string executable;
    private readonly string? desktop;

    public WindowsGpuWindowActionRuntime()
        : this(Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", "ResourceManager.GpuWindowAction.exe"), null) { }

    internal WindowsGpuWindowActionRuntime(string executable, string? desktop)
    {
        this.executable = Path.GetFullPath(executable);
        this.desktop = desktop;
    }

    internal WindowsGpuWindowActionExecutor Create(GpuWindowActionProcessLimits limits, CancellationToken cancellationToken, ulong deadlineMilliseconds)
        => new(executable, limits, cancellationToken, desktop, deadlineMilliseconds);
}
