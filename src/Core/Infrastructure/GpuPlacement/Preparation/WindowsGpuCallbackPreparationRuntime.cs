using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

public sealed class WindowsGpuCallbackPreparationRuntime
{
    internal const string FileName = "ResourceManager.GpuPlacementPreparation.exe";
    private readonly string executable;
    private readonly ILogger? logger;

    public WindowsGpuCallbackPreparationRuntime(ILogger<WindowsGpuCallbackPreparationRuntime>? logger = null)
        : this(Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", FileName), logger) { }

    internal WindowsGpuCallbackPreparationRuntime(string executable, ILogger? logger = null)
    {
        this.executable = Path.GetFullPath(executable);
        this.logger = logger;
    }

    internal bool Available => File.Exists(executable);

    internal WindowsOpenGlCallbackPreparation Create(GpuWindowActionProcessLimits limits,
        CancellationToken cancellationToken, ulong deadlineMilliseconds)
        => new(executable, limits, cancellationToken, deadlineMilliseconds, logger);
}
