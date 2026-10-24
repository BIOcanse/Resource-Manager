using System.Diagnostics;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlCrashRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VanishedOwnerIsRecoveredAtBackendStartBeforeANewWrite(bool failRestore)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-inprocess-recovery-" + Guid.NewGuid().ToString("N"));
        var stateDirectory = Path.Combine(root, "UserData", "Control", "FanControlCore");
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, "FanControlCoreProbe", "FanControlCoreProbe.dll");
            Assert.True(File.Exists(probe), "The package-backed crash fixture must be built with the test project.");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(probe); start.ArgumentList.Add("--crash"); start.ArgumentList.Add(stateDirectory);
            using (var child = Process.Start(start)!)
            {
                var error = child.StandardError.ReadToEndAsync();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { if (!child.HasExited) child.Kill(); throw; }
                Assert.True(child.ExitCode == 0, await error);
            }
            Assert.Equal(["set:0"], File.ReadAllLines(Path.Combine(stateDirectory, "operations")));
            var backend = new DurableBackend(stateDirectory, failRestore);
            await using var client = new FanControlCoreClient(new FanTestEnvironment(Path.Combine(root, "ResourceManager-APP")),
                (state, _) => new LibraryFanSession(new FanCore(backend,
                    new FanCoreOptions(state, MachineIdentity: () => "fixture-machine"))));
            var lifetime = new FanControlCoreLifetimeService(client);
            await lifetime.StartAsync(default);
            await lifetime.StartupTask;
            Assert.Equal(failRestore ? new[] { "set:0" } : ["set:0", "auto:0"],
                File.ReadAllLines(Path.Combine(stateDirectory, "operations")));
            var response = await client.SendAsync("set", new Dictionary<string, object?> { ["fan"] = 0, ["percent"] = 60 }, default);
            Assert.Equal(!failRestore, response!.Value.GetProperty("ok").GetBoolean());
            if (failRestore)
            {
                Assert.Equal(["set:0"], File.ReadAllLines(Path.Combine(stateDirectory, "operations")));
                Assert.Contains("recovery", response.Value.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Equal(["set:0", "auto:0", "set:0"], File.ReadAllLines(Path.Combine(stateDirectory, "operations")));
            await lifetime.StopAsync(default);
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(stateDirectory, "temporary-takeovers.json")));
            Assert.Equal(failRestore ? 1 : 0, saved.RootElement.GetProperty("Fans").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(stateDirectory)) File.Delete(file);
                Directory.Delete(stateDirectory);
                Directory.Delete(Path.Combine(root, "UserData", "Control"));
                Directory.Delete(Path.Combine(root, "UserData"));
                Directory.Delete(root);
            }
        }
    }

    private sealed class DurableBackend(string path, bool failRestore) : IFanBackend
    {
        public string Name => "rm-durable-probe";
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU fan", true, true, "cpu")];
        public IReadOnlyList<FanState> Read() => [new(0, "CPU fan", 2000, null, null, true, true, true)];
        public bool TrySetDuty(int fan, double percent, out string? reason)
        { File.AppendAllLines(Path.Combine(path, "operations"), ["set:0"]); reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
        public bool TrySetAutomatic(int fan, out string? reason)
        {
            reason = failRestore ? "injected restoration failure" : null;
            if (failRestore) return false;
            File.AppendAllLines(Path.Combine(path, "operations"), ["auto:0"]); return true;
        }
        public void Dispose() { }
    }
}
