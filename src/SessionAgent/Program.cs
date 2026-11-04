using ResourceManager.Shared.Runtime.Sessions;
using ResourceManager.Shared.Packages;
namespace ResourceManager.SessionAgent;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = SessionAgentPackageRoot.Resolve(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT"));
            using var packageUse = PackageUseGuard.Acquire(root);
            return UserShellProcessLauncher.TryExecute(args) ?? 64;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            return 2;
        }
    }
}
