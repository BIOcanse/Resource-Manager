using System.Text.Json;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed partial class SoftwareDataMigrationManager(IHostEnvironment environment) : ISoftwareDataMigrationManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExecutableExtensions =
    [
        ".exe",
        ".dll",
        ".sys",
        ".msi",
        ".bat",
        ".cmd",
        ".ps1"
    ];

    private static readonly string[] LowLevelPathHints =
    [
        "driver",
        "drivers",
        "anti-cheat",
        "anticheat",
        "battleye",
        "easyanticheat",
        "vanguard",
        "kernel",
        "service"
    ];

    private readonly SemaphoreSlim recordGate = new(1, 1);
    private readonly string packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);

    private string RecordPath => Path.Combine(packageRoot, "Config", "migration-records.json");

    public MigrationRoots GetRoots()
    {
        var miscRoot = Path.Combine(packageRoot, "Misc");
        return new MigrationRoots(
            Path.Combine(packageRoot, "UserData"),
            miscRoot,
            Path.Combine(packageRoot, "Dependencies"),
            Path.Combine(miscRoot, "SoftwareRoots"));
    }
}
