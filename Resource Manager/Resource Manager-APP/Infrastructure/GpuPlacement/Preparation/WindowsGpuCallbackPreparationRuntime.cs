using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

public sealed class WindowsGpuCallbackPreparationRuntime
{
    internal const string FileName = "ResourceManager.GpuPlacementPreparation.exe";
    private readonly string executable;

    public WindowsGpuCallbackPreparationRuntime()
        : this(Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", FileName)) { }

    internal WindowsGpuCallbackPreparationRuntime(string executable) => this.executable = Path.GetFullPath(executable);

    internal bool Available => File.Exists(executable);

    internal WindowsOpenGlCallbackPreparation Create(GpuWindowActionProcessLimits limits,
        CancellationToken cancellationToken, ulong deadlineMilliseconds)
        => new(executable, limits, cancellationToken, deadlineMilliseconds);
}
