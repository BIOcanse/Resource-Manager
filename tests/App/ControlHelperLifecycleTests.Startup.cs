using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    [Fact]
    public async Task DisposalDuringStartupClosesLateProcessWithoutSendingCommand()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-helper-starting-" + Guid.NewGuid().ToString("N"))).FullName;
        var dependencies = Directory.CreateDirectory(Path.Combine(root, "Dependencies")).FullName;
        var helperDirectory = Directory.CreateDirectory(Path.Combine(dependencies, "hardware-bridge")).FullName;
        var placeholder = Path.Combine(helperDirectory, "ResourceManager.HardwareBridge.exe");
        File.WriteAllText(placeholder, "test-only factory placeholder");
        var commandReceived = Path.Combine(root, "command-received");
        var finished = Path.Combine(root, "finished");
        var script = $$"""
            $ErrorActionPreference='Stop'
            if ($null -ne ($line=[Console]::In.ReadLine())) {
                [IO.File]::WriteAllText('{{commandReceived}}', 'unexpected command')
                $request=$line | ConvertFrom-Json
                [Console]::Out.WriteLine((@{id=$request.id; ok=$true} | ConvertTo-Json -Compress))
                [Console]::Out.Flush()
                while ($null -ne [Console]::In.ReadLine()) { }
            }
            [IO.File]::WriteAllText('{{finished}}', 'input closed')
            """;
        Process? helper = null;
        Process? observer = null;
        var startupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowStartupReturn = new ManualResetEventSlim();
        Process? Start(ProcessStartInfo ignored)
        {
            _ = ignored;
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            helper = Process.Start(info)!;
            observer = Process.GetProcessById(helper.Id);
            _ = observer.SafeHandle;
            startupReached.SetResult();
            if (!allowStartupReturn.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test factory was not released");
            return helper;
        }
        var contentRoot = Directory.CreateDirectory(Path.Combine(root, "ResourceManager-APP")).FullName;
        var environment = new EnvironmentInfo(contentRoot);
        var client = new HardwareBridgeClient(environment, Start);
        Func<Task<JsonElement?>> send = () => client.SendAsync("owned-test", null, CancellationToken.None);
        var pending = Task.Run(send);
        try
        {
            await startupReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            client.Dispose(); // Must not wait for the factory or lose ownership of its late result.
            allowStartupReturn.Set();
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(commandReceived));
            Assert.True(File.Exists(finished));
            Assert.Equal(0, observer.ExitCode);
        }
        finally
        {
            allowStartupReturn.Set();
            client.Dispose();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            if (observer is not null && !observer.HasExited)
            {
                try { helper!.StandardInput.Close(); } catch (InvalidOperationException) { }
                try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { observer.Kill(); await observer.WaitForExitAsync(); }
            }
            observer?.Dispose(); helper?.Dispose();
            foreach (var file in new[] { commandReceived, finished, placeholder }) File.Delete(file);
            foreach (var directory in new[] { helperDirectory, dependencies, contentRoot, root }) Directory.Delete(directory);
        }
    }
}
