using System.Diagnostics;

namespace Resource_Manager_APP.Tests;

public sealed class NativeInitializationFailureTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_INITIALIZATION_TEST", "1")]
    public Task LedgerFailureSurvivesFinalization() => RunAsync("ledger");
    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_INITIALIZATION_TEST", "1")]
    public Task SchedulerFailureSurvivesFinalization() => RunAsync("scheduler");

    private static async Task RunAsync(string kind)
    {
        var probe = Environment.GetEnvironmentVariable("RM_NATIVE_INITIALIZATION_PROBE");
        var shim = Environment.GetEnvironmentVariable("RM_NATIVE_INITIALIZATION_SHIM");
        Assert.True(File.Exists(probe), "Build NativeInitializationFailureProbe before enabling this gate.");
        Assert.True(File.Exists(shim), "Build AdapterInitializationFailureShim.cpp before enabling this gate.");
        var actual = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(probe!))!, "ResourceManager.Adapter.Native.dll");
        Assert.True(File.Exists(actual));
        var root = Path.Combine(Path.GetTempPath(), "rm-initialization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetDirectoryName(Path.GetFullPath(probe!))!;
            foreach (var name in new[] { "NativeInitializationFailureProbe.dll", "NativeInitializationFailureProbe.deps.json",
                         "NativeInitializationFailureProbe.runtimeconfig.json", "ResourceManager.Adapter.SharedMemory.dll",
                         "ResourceManager.Adapter.Abstractions.dll" })
                File.Copy(Path.Combine(source, name), Path.Combine(root, name));
            var isolatedShim = Path.Combine(root, "ResourceManager.Adapter.Native.dll");
            var isolatedActual = Path.Combine(root, "ResourceManager.Adapter.Actual.dll");
            File.Copy(shim!, isolatedShim);
            File.Copy(actual, isolatedActual);
            using var child = Process.Start(new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { Path.Combine(root, "NativeInitializationFailureProbe.dll"), kind, isolatedShim, isolatedActual }
            })!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.ExitCode == 0, $"Owned {kind} fixture exited {child.ExitCode}: {await errors}\n{await output}");
            Assert.Contains($"{kind}:32-native-initialization-failures-finalized", await output);
            Assert.Equal(string.Empty, await errors);
        }
        finally
        {
            var files = Directory.GetFiles(root);
            Assert.True(files.Length <= 7);
            foreach (var file in files) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
