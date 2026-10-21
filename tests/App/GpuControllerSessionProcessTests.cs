using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace Resource_Manager_APP.Tests;

public sealed class GpuControllerSessionProcessTests
{
    [EnvironmentVariableFact("RM_GPU_CONTROLLER_SESSION_PROBE")]
    public async Task ExplicitRecoverySignalsOnlyPinnedProductController()
    {
        var image = Environment.GetEnvironmentVariable("RM_GPU_CONTROLLER_SESSION_PROBE");
        Assert.False(string.IsNullOrWhiteSpace(image));
        using var current = Process.GetCurrentProcess();
        var root = Path.Combine(Path.GetTempPath(), "rm-recovery-test-" + Guid.NewGuid().ToString("N"));
        var attempt = Path.Combine(root, "attempt");
        Directory.CreateDirectory(attempt);
        try
        {
            var target = new GpuPlacementProcessInstance(current.Id, (ulong)current.StartTime.ToFileTimeUtc(),
                current.ProcessName, current.MainModule!.FileName);
            using var controller = WindowsExternalGpuControllerProcess.Start(target, image, ["--await-recovery", attempt], attempt);
            await controller.Ready.WaitAsync(TimeSpan.FromSeconds(8));
            var output = controller.StandardOutput.ReadToEndAsync();
            var errors = controller.StandardError.ReadToEndAsync();
            var identity = new GpuPlacementProcessInstance(controller.Process.Id, (ulong)controller.Process.StartTime.ToFileTimeUtc(),
                controller.Process.ProcessName, image);
            await File.WriteAllTextAsync(Path.Combine(attempt, "controller.json"), JsonSerializer.Serialize(identity));
            await Assert.ThrowsAsync<InvalidDataException>(() => ExternalGpuControllerRecovery.RecoverAsync(root,
                ["C:\\not-the-product.exe"], TimeSpan.FromSeconds(3), default));
            Assert.False(File.Exists(Path.Combine(attempt, "stop.recover")));
            await ExternalGpuControllerRecovery.RecoverAsync(root, [image], TimeSpan.FromSeconds(5), default);
            Assert.Contains("owned-cleanup-request-observed", await output);
            Assert.Equal(0, controller.Process.ExitCode);
            await errors;
        }
        finally
        {
            foreach (var file in Directory.GetFiles(attempt)) File.Delete(file);
            Directory.Delete(attempt); Directory.Delete(root);
        }
    }

    [Fact]
    public void CleanupSummarySettlesRecoveryWithoutClaimingMigrationPassed()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("test.exe");
        events.Accept("{\"kind\":\"recoveryRequired\",\"error\":\"first cleanup failed\"}");
        Assert.True(events.RecoveryRequired);
        events.Accept("{\"kind\":\"cleanupRecovered\",\"cleanupPassed\":true,\"rootRestored\":true}");
        events.Accept("{\"kind\":\"summary\",\"passed\":false,\"cleanupPassed\":true,\"rootRestored\":true}");
        Assert.False(events.RecoveryRequired);
        Assert.False(events.Summary!.Passed);
        Assert.True(events.Summary.CleanupPassed);
        Assert.Equal("first cleanup failed", events.Error);
    }
    [EnvironmentVariableFact("RM_GPU_CONTROLLER_SESSION_PROBE")]
    public async Task OwnedProbeRunsInTargetSessionAndBothPipesDrainWithoutInheritedHandles()
    {
        var image = Environment.GetEnvironmentVariable("RM_GPU_CONTROLLER_SESSION_PROBE");
        Assert.False(string.IsNullOrWhiteSpace(image));
        using var current = Process.GetCurrentProcess();
        var root = Path.Combine(Path.GetTempPath(), "rm-controller-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = new GpuPlacementProcessInstance(current.Id, (ulong)current.StartTime.ToFileTimeUtc(),
                current.ProcessName, current.MainModule!.FileName);
            using var controller = WindowsExternalGpuControllerProcess.Start(target, image,
                ["space and trailing\\", "quote\"and\\slash"], root);
            await controller.Ready.WaitAsync(TimeSpan.FromSeconds(8));
            var output = controller.StandardOutput.ReadToEndAsync();
            var errors = controller.StandardError.ReadToEndAsync();
            await controller.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, controller.Process.ExitCode);
            var text = await output;
            Assert.Contains($"session:{current.SessionId}", text);
            Assert.Contains("argument:space and trailing\\", text);
            Assert.Contains("argument:quote\"and\\slash", text);
            Assert.Contains("owned-probe-error-stream", await errors);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
