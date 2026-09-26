using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionParentDeathTests(ITestOutputHelper output)
{
    [WindowWorkerParentTheory]
    [InlineData("suspended")]
    [InlineData("connected")]
    [InlineData("suspended-window")]
    [InlineData("connected-window")]
    public async Task ParentDeathClosesItsJobWithoutASecondOwnerTerminatingTheWorker(string stage)
    {
        var parentPath = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_PARENT_PROBE")!);
        var workerPath = Path.GetFullPath(Environment.GetEnvironmentVariable(
            stage.EndsWith("-window", StringComparison.Ordinal) ? "RM_GPU_WINDOW_ACTION" : "RM_GPU_WINDOW_WORKER_PROBE")!);
        using var testHost = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(testHost.SafeHandle, out var testCreation, out _, out _, out _));
        output.WriteLine("testHost=" + JsonSerializer.Serialize(new { processId = Environment.ProcessId, creationFileTimeUtc = testCreation }));
        using var parent = new Process
        {
            StartInfo = new(parentPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(parentPath)!
            }
        };
        parent.StartInfo.ArgumentList.Add(workerPath);
        parent.StartInfo.ArgumentList.Add(stage);
        Assert.True(parent.Start());
        Process? worker = null;
        try
        {
            Assert.True(GetProcessTimes(parent.SafeHandle, out var parentCreation, out _, out _, out _));
            output.WriteLine("parentDeathParent=" + JsonSerializer.Serialize(new { processId = parent.Id, creationFileTimeUtc = parentCreation }));
            using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var readyText = await parent.StandardOutput.ReadLineAsync(readinessDeadline.Token);
            Assert.NotNull(readyText);
            using var ready = JsonDocument.Parse(readyText);
            output.WriteLine("parentDeathReady=" + ready.RootElement.GetRawText());
            Assert.Equal(parent.Id, ready.RootElement.GetProperty("parent").GetProperty("processId").GetInt32());
            Assert.Equal(parentCreation, ready.RootElement.GetProperty("parent").GetProperty("creationFileTimeUtc").GetInt64());
            Assert.Equal(stage, ready.RootElement.GetProperty("stage").GetString());
            Assert.Equal(1u, ready.RootElement.GetProperty("activeProcessCount").GetUInt32());
            var identity = ready.RootElement.GetProperty("worker");
            var candidate = Process.GetProcessById(identity.GetProperty("ProcessId").GetInt32());
            try
            {
                Assert.True(GetProcessTimes(candidate.SafeHandle, out var creation, out _, out _, out _));
                Assert.Equal(identity.GetProperty("CreationFileTimeUtc").GetInt64(), creation);
                worker = candidate;
            }
            finally { if (worker is null) candidate.Dispose(); }
            Assert.False(worker.HasExited);
            if (stage.EndsWith("-window", StringComparison.Ordinal))
                Assert.NotEqual(0UL, ready.RootElement.GetProperty("transferredParentHandle").GetUInt64());
            if (stage == "connected")
            {
                var hello = ready.RootElement.GetProperty("hello");
                Assert.Equal(worker.Id, hello.GetProperty("pid").GetInt32());
                Assert.Equal(32771u, hello.GetProperty("errorMode").GetUInt32());
                Assert.Equal(1u, hello.GetProperty("activeProcessCount").GetUInt32());
                Assert.Single(hello.GetProperty("members").EnumerateArray());
            }
            var elapsed = Stopwatch.StartNew();
            parent.Kill();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var stderr = await parent.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(stderr);
            output.WriteLine("parentDeathCleanup=" + JsonSerializer.Serialize(new
            {
                stage, parentExitCode = parent.ExitCode, workerExitCode = worker.ExitCode,
                parentExited = parent.HasExited, workerExited = worker.HasExited,
                workerKilledByTest = false, elapsedMilliseconds = elapsed.ElapsedMilliseconds
            }));
        }
        finally
        {
            if (!parent.HasExited)
            {
                output.WriteLine("unexpectedParentCleanup=true");
                parent.Kill();
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (worker is not null)
            {
                if (!worker.HasExited)
                {
                    output.WriteLine("unexpectedWorkerCleanup=true");
                    worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                worker.Dispose();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}

internal sealed class WindowWorkerParentTheoryAttribute : TheoryAttribute
{
    public WindowWorkerParentTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_PARENT_PROBE"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_WORKER_PROBE"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION")))
            Skip = "Explicit parent and native worker probe paths are required.";
    }
}
