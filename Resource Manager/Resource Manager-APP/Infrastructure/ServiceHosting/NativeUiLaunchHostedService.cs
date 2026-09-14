using System.ComponentModel;
using ResourceManager.App.Application.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class NativeUiLaunchHostedService(
    IInteractiveUserSessionBroker sessionBroker,
    IRuntimePlanProvider plans,
    ILogger<NativeUiLaunchHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SessionScanInterval = TimeSpan.FromSeconds(5);
    private readonly InteractiveUserSessionActivationTracker activationTracker = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nativeUiPath = ResolveNativeUiPath();
        if (!File.Exists(nativeUiPath))
        {
            logger.LogError(
                "SCM backend cannot find the Native UI entry at {Path}.",
                nativeUiPath);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            ScanActiveSessions(nativeUiPath);
            await Task.Delay(SessionScanInterval, stoppingToken);
        }
    }

    internal void ScanActiveSessions(string nativeUiPath)
    {
        if (plans.Current.Version == 0) return;
        IReadOnlyList<uint> newlyActive;
        try
        {
            newlyActive = activationTracker.Observe(
                sessionBroker.GetActiveSessionIds());
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "Cannot enumerate active Windows sessions.");
            return;
        }

        foreach (var sessionId in newlyActive)
        {
            if (!plans.Current.AutoStartEnabled) continue;
            try
            {
                var presence = sessionBroker.GetNativeUiProcessPresence(
                    sessionId,
                    nativeUiPath);
                if (presence == NativeUiProcessPresence.Expected)
                {
                    logger.LogInformation(
                        "Native UI is already present in Windows session {SessionId}.",
                        sessionId);
                    continue;
                }

                if (presence == NativeUiProcessPresence.Conflicting)
                {
                    logger.LogError(
                        "Refusing to launch Native UI in Windows session {SessionId} because a conflicting executable identity is present.",
                        sessionId);
                    continue;
                }

                var processId = sessionBroker.LaunchNativeUi(
                    sessionId,
                    nativeUiPath,
                    "--background-startup");
                logger.LogInformation(
                    "Started standard-user Native UI in Windows session {SessionId}, PID {ProcessId}.",
                    sessionId,
                    processId);
            }
            catch (Exception ex) when (ex is Win32Exception
                                       or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or FileNotFoundException)
            {
                activationTracker.MarkLaunchFailed(sessionId);
                logger.LogError(
                    ex,
                    "Cannot start Native UI in Windows session {SessionId}.",
                    sessionId);
            }
        }
    }

    private static string ResolveNativeUiPath()
    {
        var candidates = ResolveNativeUiPathCandidates();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IReadOnlyList<string> ResolveNativeUiPathCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(
                baseDirectory,
                "..",
                "ResourceManagerNativeUi",
                "ResourceManager.NativeUi.exe")
        };

        if (TryFindAppRoot(baseDirectory, out var appRoot))
        {
            foreach (var configuration in ResolveBuildConfigurations(baseDirectory))
            {
                candidates.Add(Path.Combine(
                    appRoot,
                    "NativeUi",
                    "bin",
                    configuration,
                    "net10.0-windows",
                    "ResourceManager.NativeUi.exe"));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveBuildConfigurations(
        string baseDirectory)
    {
        var parts = baseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(static part => part.Equals(
            "Release",
            StringComparison.OrdinalIgnoreCase))
                ? ["Release", "Debug"]
                : ["Debug", "Release"];
    }

    private static bool TryFindAppRoot(
        string startPath,
        out string appRoot)
    {
        for (var directory = new DirectoryInfo(startPath);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "ResourceManager.App.csproj")))
            {
                appRoot = directory.FullName;
                return true;
            }
        }

        appRoot = string.Empty;
        return false;
    }
}
