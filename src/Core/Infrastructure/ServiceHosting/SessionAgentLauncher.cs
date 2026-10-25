using ResourceManager.App.Domain.Software;
using ResourceManager.Shared.Runtime;
namespace ResourceManager.App.Infrastructure.ServiceHosting;

// Business consumers pass only a typed action and its attested desktop caller.
public static class SessionAgentLauncher
{
    public static uint Launch(IInteractiveUserSessionBroker broker, SoftwareOperationCaller? caller,
        UserShellExecuteRequest request)
    {
        UserSessionCallerValidation.RequireActiveUser(caller, broker);
        return broker.LaunchUserShell(caller!.WindowsSessionId, caller.WindowsSid,
            ResolveExecutablePath(), Arguments(request));
    }

    public static Task<int> LaunchAndWaitAsync(IInteractiveUserSessionBroker broker, SoftwareOperationCaller? caller,
        UserShellExecuteRequest request, CancellationToken cancellationToken)
    {
        UserSessionCallerValidation.RequireActiveUser(caller, broker);
        return broker.LaunchUserShellAndWaitAsync(caller!.WindowsSessionId, caller.WindowsSid,
            ResolveExecutablePath(), Arguments(request), cancellationToken);
    }

    private static string Arguments(UserShellExecuteRequest request)
        => UserShellExecuteRequest.Argument + " " + request.Encode();

    internal static string ResolveExecutablePath()
    {
        var packageRoot = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(packageRoot))
            return Path.GetFullPath(Path.Combine(packageRoot, "Bin", "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe"));
        var local = Path.Combine(AppContext.BaseDirectory, "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe");
        if (File.Exists(local)) return local;
        var published = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe"));
        if (File.Exists(published)) return published;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 32; depth++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "ResourceManager.App.csproj"))) continue;
            var release = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("Release", StringComparer.OrdinalIgnoreCase);
            foreach (var configuration in release ? new[] { "Release", "Debug" } : new[] { "Debug", "Release" })
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.FullName, "..", "SessionAgent", "bin",
                    configuration, "net10.0-windows", "ResourceManager.SessionAgent.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            break;
        }
        return published;
    }
}
