using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

internal sealed class StartupModuleTestCatalog : IHostEnvironment, IDisposable
{
    private readonly TrustedAccessTemporaryDirectory temporary = new();
    public string Root => temporary.Root;
    public string Image => Path.Combine(Root, "Bin", "Tools", "arbitrary.dll");
    public PackageRuntimeCatalog Catalog { get; }
    public StartupModuleTestCatalog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Image)!);
        File.WriteAllBytes(Image, [1, 2, 3]);
        var module = new PackageStartupModule("fixture-module", "preload", Asset("Bin/Tools/arbitrary.dll"));
        File.WriteAllText(Path.Combine(Root, "runtime-modules.json"),
            JsonSerializer.Serialize(new { version = 1, modules = new[] { module } }, JsonSerializerOptions.Web));
        WriteRelease();
        Catalog = PackageRuntimeCatalog.Load(Root);
    }
    public PackageRuntimeAsset Asset(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        return new(relative, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    public void WriteFrontendModules()
    {
        File.WriteAllBytes(Path.Combine(Root, "Bin", "Tools", "layer.dll"), [7, 8, 9]);
        File.WriteAllText(Path.Combine(Root, "Bin", "Tools", "layer.json"),
            """{"layer":{"name":"VK_LAYER_frontend_test","library_path":"layer.dll"}}""");
        var modules = new[]
        {
            new PackageStartupModule("performance-overlay-preload", "preload", Asset("Bin/Tools/arbitrary.dll")),
            new PackageStartupModule("performance-overlay-vulkan", "vulkan-layer", Asset("Bin/Tools/layer.dll"),
                Asset("Bin/Tools/layer.json"), "VK_LAYER_frontend_test")
        };
        File.WriteAllText(Path.Combine(Root, "runtime-modules.json"), JsonSerializer.Serialize(new { version = 1, modules }, JsonSerializerOptions.Web));
        WriteRelease("Bin/Tools/layer.dll", "Bin/Tools/layer.json");
    }
    public void WriteRelease(params string[] extra) => File.WriteAllText(Path.Combine(Root, "release-manifest.json"),
        JsonSerializer.Serialize(new { files = new[] { "Bin/Tools/arbitrary.dll", "runtime-modules.json" }.Concat(extra)
            .Select(relative => { var asset = Asset(relative); return new { path = relative, length = asset.Length, sha256 = asset.Sha256 }; }) }));
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "StartupModules.Tests";
    public string ContentRootPath { get => Root; set => throw new NotSupportedException(); }
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public void Dispose() => temporary.Dispose();
}

internal sealed class EmptyStartupModuleSettingsStore : IStartupModuleSettingsStore
{
    public Task<StartupModuleSettingsDocument> GetAsync(CancellationToken token) => Task.FromResult(new StartupModuleSettingsDocument(1, []));
    public Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken token) => throw new NotSupportedException();
}
internal sealed class FailingStartupModuleSettingsStore : IStartupModuleSettingsStore
{
    public Task<StartupModuleSettingsDocument> GetAsync(CancellationToken token) => Task.FromException<StartupModuleSettingsDocument>(new IOException("Unavailable fixture."));
    public Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken token) => throw new NotSupportedException();
}
