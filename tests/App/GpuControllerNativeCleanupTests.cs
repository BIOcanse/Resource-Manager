using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuControllerNativeCleanupTests(ITestOutputHelper log)
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_GPU_CLEANUP_TEST", "1")]
    public Task RestorationFailureRecoversOwnedNativeTarget() => RunAsync("restore");

    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_GPU_CLEANUP_TEST", "1")]
    public Task ContinuationFailureRecoversOwnedNativeTarget() => RunAsync("continue");

    private async Task RunAsync(string fault)
    {
        Assert.True(OperatingSystem.IsWindows());
        var image = Environment.GetEnvironmentVariable("RM_GPU_CONTROLLER_CLEANUP_PROBE");
        Assert.False(string.IsNullOrWhiteSpace(image));
        Assert.True(File.Exists(image), "Build tests/Native/GpuControllerCleanupProbe.cpp before enabling this gate.");
        image = Path.GetFullPath(image!);
        using var current = Process.GetCurrentProcess();
        var root = Path.Combine(Path.GetTempPath(), "rm-native-cleanup-" + Guid.NewGuid().ToString("N"));
        var attempt = Path.Combine(root, "attempt");
        Directory.CreateDirectory(attempt);
        try
        {
            var target = new GpuPlacementProcessInstance(current.Id, (ulong)current.StartTime.ToFileTimeUtc(),
                current.ProcessName, current.MainModule!.FileName);
            using var controller = WindowsExternalGpuControllerProcess.Start(target, image,
                ["--cleanup-probe", fault, attempt], attempt);
            await controller.Ready.WaitAsync(TimeSpan.FromSeconds(8));
            var output = controller.StandardOutput.ReadToEndAsync();
            var errors = controller.StandardError.ReadToEndAsync();
            try
            {
                var identity = new GpuPlacementProcessInstance(controller.Process.Id,
                    (ulong)controller.Process.StartTime.ToFileTimeUtc(), controller.Process.ProcessName, image);
                await File.WriteAllTextAsync(Path.Combine(attempt, "controller.json"), JsonSerializer.Serialize(identity));
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(attempt, "cleanup.failed")) && !controller.Process.HasExited
                    && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
                if (controller.Process.HasExited)
                    Assert.Fail($"Native cleanup did not reach its expected fault: {await errors}\n{await output}");
                Assert.True(File.Exists(Path.Combine(attempt, "cleanup.failed")), "Owned native fault deadline.");
                Assert.False(File.Exists(Path.Combine(attempt, "target.completed")));
                await Assert.ThrowsAsync<InvalidDataException>(() => ExternalGpuControllerRecovery.RecoverAsync(root,
                    [Path.Combine(root, "wrong-controller.exe")], TimeSpan.FromSeconds(1), default));
                Assert.False(File.Exists(Path.Combine(attempt, "stop.recover")));
                await ExternalGpuControllerRecovery.RecoverAsync(root, [image], TimeSpan.FromSeconds(8), default);
                Assert.Equal(0, controller.Process.ExitCode);
                Assert.True(File.Exists(Path.Combine(attempt, "target.completed")));
                var receipts = await output;
                var diagnostics = await errors;
                log.WriteLine(receipts);
                log.WriteLine(diagnostics);
                Assert.Contains("owned-cleanup-recovery-passed", receipts);
                Assert.Equal(string.Empty, diagnostics);
            }
            finally
            {
                // Only this finite owned probe. Its job closes and releases its
                // exact child even if the host assertion fails.
                await controller.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                await Task.WhenAll(output, errors);
            }
        }
        finally
        {
            foreach (var name in new[] { "controller.json", "cleanup.failed", "stop.cancel", "stop.recover",
                "target.release", "target.completed" })
            {
                var file = Path.Combine(attempt, name);
                if (File.Exists(file)) File.Delete(file);
            }
            Directory.Delete(attempt);
            Directory.Delete(root);
        }
    }
}
