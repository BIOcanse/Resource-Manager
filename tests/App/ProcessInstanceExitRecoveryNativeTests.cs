using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessInstanceExitRecoveryNativeTests(ITestOutputHelper output)
{
    [WindowWorkerTheory]
    [InlineData(0U)]
    [InlineData(259U)]
    public async Task RecoveryDistinguishesLiveProcessFromExitedProcessWithRetainedHandle(uint exitCode)
    {
        await using var execution = WindowsGpuWindowActionProcess.Create(
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 16384, 4096), CancellationToken.None);
        execution.StartSuspended(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_WORKER_PROBE")!, ["normal"]);
        var identity = execution.Identity!;
        var reader = new WindowsProcessResourcePolicyWriter(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256));
        try
        {
            var live = reader.ReadProcessInstanceForRecovery(identity.ProcessId);
            Assert.Equal(RecoveryReadStatus.Found, live.Status);
            Assert.Equal(identity.CreationFileTimeUtc, live.Value!.StartedAt.ToFileTime());
            using var owned = OpenProcess(0x00101001, false, identity.ProcessId);
            Assert.False(owned.IsInvalid);
            Assert.True(GetProcessTimes(owned, out var creation, out _, out _, out _));
            Assert.Equal(identity.CreationFileTimeUtc, creation);
            Assert.True(TerminateProcess(owned, exitCode));
            Assert.Equal(0U, WaitForSingleObject(owned, 5000));
            Assert.True(GetProcessTimes(owned, out _, out var exitedAt, out _, out _));
            Assert.True(exitedAt > creation);
            var exited = reader.ReadProcessInstanceForRecovery(identity.ProcessId);
            output.WriteLine("processExitRecovery=" + JsonSerializer.Serialize(new { identity, exitCode, exitedAt, live, exited }));
            Assert.Equal(RecoveryReadStatus.NotFoundOrExited, exited.Status);
            Assert.Null(exited.Value);
        }
        finally
        {
            var cleanup = await execution.StopAsync();
            output.WriteLine("processExitRecoveryCleanup=" + JsonSerializer.Serialize(new { identity, cleanup }));
            Assert.True(cleanup.Complete);
            Assert.False(cleanup.TerminationRequested);
            Assert.Equal(exitCode, cleanup.ExitCode);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
