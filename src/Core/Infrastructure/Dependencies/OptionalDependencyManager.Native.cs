using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private OptionalDependencyStatus BuildStatus(OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware)
        => nativeProbe.BuildStatus(definition, installedSoftware, packageRoot, logger);

    public void Dispose()
    {
        // DI owns an injected shared holder; direct callers own only the holder created here.
        if (ownsProbeResults) probeResults.Dispose();
    }
}
