using ResourceManager.Shared.Packages;

namespace ResourceManager.NativeUi;

// Process scope includes the hand-off to the resident, startup failures and full context disposal.
internal sealed class NativeUiPackageUseLifetime : IDisposable
{
    private readonly IDisposable guard;

    public NativeUiPackageUseLifetime(string? packageRoot = null)
        => guard = PackageUseGuard.Acquire(packageRoot ?? NativeUiPaths.PackageRoot);

    public void Dispose() => guard.Dispose();
}
