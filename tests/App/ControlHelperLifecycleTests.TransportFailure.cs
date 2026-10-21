using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedChannelRetainsProcessUntilEofCleanupFinishes(bool fan)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-helper-channel-" + Guid.NewGuid().ToString("N"))).FullName;
        var dependencies = Directory.CreateDirectory(Path.Combine(root, "Dependencies")).FullName;
        var helperDirectory = Directory.CreateDirectory(Path.Combine(dependencies, fan ? "fan-control-core" : "hardware-bridge")).FullName;
        var placeholder = Path.Combine(helperDirectory, fan ? "FanControlCore.exe" : "ResourceManager.HardwareBridge.exe");
        File.WriteAllText(placeholder, "test-only factory placeholder");
        var cleaning = Path.Combine(root, "cleaning");
        var release = Path.Combine(root, "release");
        var finished = Path.Combine(root, "finished");
        var observers = new List<Process>();
        var helpers = new List<Process>();
        var launches = 0;
        Process? Start(ProcessStartInfo ignored)
        {
            _ = ignored;
            var fail = ++launches == 1;
            var script = $$"""
                $ErrorActionPreference='Stop'
                $request=[Console]::In.ReadLine() | ConvertFrom-Json
                if (${{fail.ToString().ToLowerInvariant()}}) {
                    [Console]::Out.WriteLine('invalid-json')
                } else {
                    [Console]::Out.WriteLine((@{id=$request.id; ok=$true} | ConvertTo-Json -Compress))
                }
                [Console]::Out.Flush()
                if ($null -ne [Console]::In.ReadLine()) { throw 'Expected EOF before another command' }
                if (${{fail.ToString().ToLowerInvariant()}}) {
                    [IO.File]::WriteAllText('{{cleaning}}', 'cleanup still owns this process')
                    $deadline=[DateTime]::UtcNow.AddSeconds(20)
                    while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
                    [IO.File]::WriteAllText('{{finished}}', 'cleanup complete')
                }
                """;
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var helper = Process.Start(info)!;
            helpers.Add(helper);
            var observer = Process.GetProcessById(helper.Id);
            _ = observer.SafeHandle;
            observers.Add(observer);
            return helper;
        }
        var contentRoot = Directory.CreateDirectory(Path.Combine(root, "ResourceManager-APP")).FullName;
        var environment = new EnvironmentInfo(contentRoot);
        IDisposable client;
        Func<Task<JsonElement?>> send;
        if (fan)
        {
            var value = new FanControlCoreClient(environment, Start); client = value;
            send = () => value.SendAsync("owned-test", null, CancellationToken.None);
        }
        else
        {
            var value = new HardwareBridgeClient(environment, Start); client = value;
            send = () => value.SendAsync("owned-test", null, CancellationToken.None);
        }
        try
        {
            Assert.Null(await send().WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitForFile(cleaning);
            Assert.False(observers[0].HasExited);
            Assert.Null(await send().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, launches); // A failed channel must not introduce a second hardware owner.
            File.WriteAllText(release, "release");
            await observers[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, observers[0].ExitCode);
            Assert.True(File.Exists(finished));
            var response = await send().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(response!.Value.GetProperty("ok").GetBoolean());
            Assert.Equal(2, launches);
            client.Dispose();
            await observers[1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, observers[1].ExitCode);
        }
        finally
        {
            File.WriteAllText(release, "release");
            client.Dispose();
            for (var index = 0; index < observers.Count; index++)
            {
                var observer = observers[index];
                if (!observer.HasExited)
                {
                    try { helpers[index].StandardInput.Close(); } catch (InvalidOperationException) { }
                    try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { observer.Kill(); await observer.WaitForExitAsync(); }
                }
                observer.Dispose(); helpers[index].Dispose();
            }
            foreach (var file in new[] { cleaning, release, finished, placeholder }) File.Delete(file);
            foreach (var directory in new[] { helperDirectory, dependencies, contentRoot, root }) Directory.Delete(directory);
        }
    }
}
