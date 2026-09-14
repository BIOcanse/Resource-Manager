namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOperationAuthorityGuardTests
{
    [Fact]
    public void OperationCoordinatorHasOneOwnerAndNoRetiredTaskAuthority()
    {
        var appRoot = FindAppRoot();
        foreach (var retired in new[]
        {
            Path.Combine("Domain", "Tasks", "ManagedTask.cs"),
            Path.Combine("Application", "Tasks", "IBackgroundTaskManager.cs"),
            Path.Combine("Infrastructure", "Tasks", "InMemoryBackgroundTaskManager.cs"),
            Path.Combine("Infrastructure", "Tasks", "BackgroundTaskContext.cs"),
            Path.Combine("Application", "Components", "IComponentTaskService.cs"),
            Path.Combine("Infrastructure", "Components", "ComponentTaskService.cs"),
            Path.Combine("Application", "Software", "ISoftwareTaskService.cs"),
            Path.Combine("Infrastructure", "Software", "SoftwareTaskService.cs"),
            Path.Combine("Endpoints", "TaskEndpoints.cs")
        })
        {
            Assert.False(File.Exists(Path.Combine(appRoot, retired)), retired);
        }

        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "RuntimeSpecializationServiceRegistration.cs"));
        Assert.Equal(
            1,
            Count(
                registration,
                "services.AddSingleton<HostManagerOperationCoordinatorOwner>();"));
        Assert.Contains(
            "GetRequiredService<HostManagerOperationCoordinatorOwner>()",
            registration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IBackgroundTaskManager", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("InMemoryBackgroundTaskManager", registration, StringComparison.Ordinal);

        var endpoints = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(appRoot, "Endpoints"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        Assert.Contains("/api/operations", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/api/operations/health",
            endpoints,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Results.Ok(operations.GetHealth())",
            endpoints,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/api/tasks", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("install-task", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall-task", endpoints, StringComparison.Ordinal);

        var clientRoot = Path.Combine(appRoot, "ClientApp", "src");
        var clientSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    clientRoot,
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(static path =>
                    path.EndsWith(".ts", StringComparison.Ordinal)
                    || path.EndsWith(".tsx", StringComparison.Ordinal))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        foreach (var retired in new[]
        {
            "BackgroundTask",
            "refreshTasks",
            "cancelTaskPolling",
            "managementActionKeyFromTask",
            "/api/tasks",
            "install-task",
            "uninstall-task"
        })
        {
            Assert.DoesNotContain(retired, clientSource, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(
            Path.Combine(sourceDirectory, "..", "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
