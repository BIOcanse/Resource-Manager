using ResourceManager.Shared.Packages;

namespace ResourceManager.NativeUi;

// Process scope includes helper modes, startup failures, the tray and full context disposal.
internal sealed class NativeUiPackageUseLifetime : IDisposable
{
    private readonly IDisposable guard;

    public NativeUiPackageUseLifetime(string? packageRoot = null)
        => guard = PackageUseGuard.Acquire(packageRoot ?? NativeUiPaths.PackageRoot);

    public void Dispose() => guard.Dispose();
}
