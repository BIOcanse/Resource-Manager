using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security.TrustedAccess;

public sealed class WindowsTrustedAccessKeyFileAccess : ITrustedAccessKeyFileAccess
{
    private readonly string packageRoot;

    public WindowsTrustedAccessKeyFileAccess(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
    }

    public string? Read() => TrustedAccessKeyFile.Read(packageRoot);

    public void WriteAtomic(string key) => TrustedAccessKeyFile.WriteAtomic(packageRoot, key);
}
