using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperPayloadTests
{
    [EnvironmentVariableFact("RM_CONTROL_HELPER_PACKAGE_DIRECTORY")]
    public Task DeliveredFanPackageInstallsAndRunsWithoutHardwareDiscovery()
        => CheckDeliveredPackageAsync("fan-control-core", verify: false);

    [EnvironmentVariableFact("RM_CONTROL_HELPER_PACKAGE_DIRECTORY")]
    public Task DeliveredBridgePackageInstallsAndRunsWithoutHardwareDiscovery()
        => CheckDeliveredPackageAsync("hardware-bridge", verify: false);

    [EnvironmentVariableFact("RM_CONTROL_HELPER_PACKAGE_DIRECTORY")]
    public Task DeliveredFanPackageVerificationAcquiresAllRuntimeFiles()
        => CheckDeliveredPackageAsync("fan-control-core", verify: true);

    [EnvironmentVariableFact("RM_CONTROL_HELPER_PACKAGE_DIRECTORY")]
    public Task DeliveredBridgePackageVerificationAcquiresAllRuntimeFiles()
        => CheckDeliveredPackageAsync("hardware-bridge", verify: true);

    private static async Task CheckDeliveredPackageAsync(string id, bool verify)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var archivePath = Path.Combine(Environment.GetEnvironmentVariable("RM_CONTROL_HELPER_PACKAGE_DIRECTORY")!,
            definition.InstallerFileName);
        Assert.True(File.Exists(archivePath), "Publish and package the current helper before running this delivery check.");
        var payload = await File.ReadAllBytesAsync(archivePath);
        using var archive = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Select(entry => entry.FullName).ToArray();
        Assert.Contains("LICENSE", files);
        Assert.Contains(definition.InstalledProbeRelativePaths[0], files);
        if (id == "hardware-bridge")
        {
            Assert.Contains("inpoutx64.dll", files);
            Assert.Contains("ThirdPartyNotices/ZenStates-Core.LICENSE", files);
        }
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-delivery-" + Guid.NewGuid().ToString("N"));
        var ownedDirectory = Path.GetFullPath(Path.Combine(root, "Dependencies", id)) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            Assert.False(Path.IsPathRooted(file), "The delivery archive must use relative paths.");
            Assert.True(Path.GetFullPath(Path.Combine(ownedDirectory, file)).StartsWith(ownedDirectory, StringComparison.OrdinalIgnoreCase),
                "The delivery archive cannot escape the owned test directory.");
        }
        try
        {
            using var handler = new ArchiveHandler(payload);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            var dependency = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance));
            var manager = new ComponentManager(dependency, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment));
            if (verify)
            {
                var result = await manager.VerifyAsync(id, default);
                // This checks delivery, not hardware readiness. The provider probe is deliberately unavailable.
                Assert.True(result.Status!.Installed);
                Assert.False(result.Status.ProviderActive);
                Assert.Equal(BackendMessageCodes.Dependency.ComponentFilesUnverified, result.Message.Code);
            }
            else
            {
                Assert.Equal("filesExtracted", (await manager.InstallAsync(id, true, null, default)).State);
            }
            Assert.Equal(1, handler.Requests);
            var directory = Path.Combine(root, "Dependencies", id);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
            {
                var installed = Path.Combine(directory, entry.FullName);
                Assert.True(File.Exists(installed), entry.FullName);
                using var expected = entry.Open();
                using var actual = File.OpenRead(installed);
                Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
            }
            using var fan = new FanControlCoreClient(environment);
            using var bridge = new HardwareBridgeClient(environment);
            Assert.True(id == "fan-control-core" ? fan.IsInstalled : bridge.IsInstalled);

            // The production helper exits here before constructing or opening any hardware backend.
            var start = new ProcessStartInfo(Path.Combine(directory, definition.InstalledProbeRelativePaths[0]))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("--runtime-check");
            start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-installed-runtime");
            start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
            using var receipt = JsonDocument.Parse(await stdout);
            Assert.True(receipt.RootElement.GetProperty("runtimeReady").GetBoolean());
        }
        finally
        {
            DeleteOwnedPackage(root, id, files, definition.InstallerFileName);
        }
    }
}
