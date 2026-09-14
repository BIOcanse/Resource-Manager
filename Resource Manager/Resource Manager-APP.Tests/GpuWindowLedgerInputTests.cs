using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowLedgerInputTests(ITestOutputHelper output)
{
    [Fact]
    public void WindowLedgerRecordsTheLoadedTestAndAppImagesAndExplicitWorkerPaths()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(current.SafeHandle, out var birth, out _, out _, out _));
        var assemblies = new[] { typeof(GpuWindowLedgerInputTests).Assembly.Location, typeof(WindowsGpuWindowActionExecutor).Assembly.Location };
        var environment = new[] { "RM_GPU_WINDOW_ACTION", "RM_GPU_WINDOW_TARGET", "RM_GPU_WINDOW_WORKER_PROBE", "RM_GPU_WINDOW_SENDER_PROBE", "RM_GPU_WINDOW_PARENT_PROBE" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var identities = assemblies.Concat(environment.Values.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            using var file = File.OpenRead(path);
            return new { path = Path.GetFullPath(path), byteLength = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(file)) };
        }).ToArray();
        output.WriteLine("windowLedgerInputs=" + JsonSerializer.Serialize(new { processId = Environment.ProcessId, creationFileTimeUtc = birth, assemblies, environment, identities }));
        Assert.Equal(2, assemblies.Length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
