namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPublicServiceAuthorityGuardTests
{
    [Fact]
    public void PublicServiceHasOneHostManagerAuthorityAndNoRetiredManagedPlanner()
    {
        var appRoot = FindAppRoot();
        var retiredFiles = new[]
        {
            Path.Combine("Infrastructure", "PublicServices", "LocalPublicServiceCatalog.cs"),
            Path.Combine("Infrastructure", "PublicServices", "LocalPublicServiceAccessPolicy.cs"),
            Path.Combine("Application", "PublicServices", "AiModels", "LocalAiModelCoordinator.cs"),
            Path.Combine("Infrastructure", "PublicServices", "AiGateway", "AiGatewayLocalModelPolicy.cs"),
            Path.Combine("Application", "PublicServices", "ILocalPublicServiceCapabilityProvider.cs"),
            Path.Combine("Infrastructure", "PublicServices", "AiModelCatalogPublicServiceCapability.cs"),
            Path.Combine("Infrastructure", "PublicServices", "BrowserRuntimePublicServiceCapability.cs"),
            Path.Combine("Infrastructure", "PublicServices", "FileIndexPublicServiceCapability.cs"),
            Path.Combine("Infrastructure", "PublicServices", "SqliteDatabasePublicServiceCapability.cs"),
            Path.Combine("Infrastructure", "PublicServices", "PublicResourceDirectoryCapability.cs")
        };
        foreach (var retired in retiredFiles)
        {
            Assert.False(File.Exists(Path.Combine(appRoot, retired)), retired);
        }

        var authorityFiles = Directory.GetFiles(
            Path.Combine(appRoot, "Infrastructure", "PublicServices"),
            "HostManagerPublicServiceCoordinatorOwner*.cs",
            SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(authorityFiles);
        var authoritySource = string.Join(
            Environment.NewLine,
            authorityFiles.OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        Assert.Contains(
            "class HostManagerPublicServiceCoordinatorOwner",
            authoritySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FrozenSet<", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogLifetime", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshGate", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("providers.FirstOrDefault", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnsPath(", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportsMethod(", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("mutationGate", authoritySource, StringComparison.Ordinal);

        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "PublicServiceRegistration.cs"));
        Assert.Contains(
            "GetRequiredService<HostManagerPublicServiceCoordinatorOwner>()",
            registration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ILocalPublicServiceCapabilityProvider",
            registration,
            StringComparison.Ordinal);
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
