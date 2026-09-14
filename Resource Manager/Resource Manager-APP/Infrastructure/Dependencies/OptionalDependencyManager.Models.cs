namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private sealed record DependencyPaths(
        string DependencyRoot,
        string InstallerDirectory,
        string InstallDirectory);

    private sealed record OptionalDependencyExternalInstall(
        string InstallDirectory,
        string Source);
}
