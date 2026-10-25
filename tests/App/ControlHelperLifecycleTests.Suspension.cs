using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    [Fact]
    public async Task CanceledSuspensionBeforeInputCloseResumesExistingHelper()
    {
        await using var helper = new SuspensionTestProcessHarness(
            TimeSpan.FromSeconds(3), FirstHelperBehavior.ServeRequests);
        var description = await helper.Client.SendAsync("describe", null, default);
        Assert.True(description!.Value.GetProperty("available").GetBoolean());

        var inputGate = GetInputGate(helper.Client);
        await inputGate.WaitAsync();
        var inputGateReleased = false;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var suspension = helper.Client.SuspendForInstallationAsync(cancellation.Token).AsTask();
            Assert.False(suspension.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await suspension.WaitAsync(TimeSpan.FromSeconds(5)));
            inputGate.Release();
            inputGateReleased = true;
        }
        finally
        {
            if (!inputGateReleased) inputGate.Release();
        }

        var response = await helper.Client.SendAsync("read", null, default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(1, helper.LaunchCount);
        Assert.False(helper.GetObserver(0).HasExited);
    }

    [Fact]
    public async Task CanceledSuspensionAfterInputCloseWaitsForOldHelperThenRestarts()
    {
        await using var helper = new SuspensionTestProcessHarness(
            TimeSpan.FromSeconds(3), FirstHelperBehavior.WaitAfterEof);
        var description = await helper.Client.SendAsync("describe", null, default);
        Assert.True(description!.Value.GetProperty("available").GetBoolean());

        using var cancellation = new CancellationTokenSource();
        var suspension = helper.Client.SuspendForInstallationAsync(cancellation.Token).AsTask();
        await WaitForFile(helper.EofObservedPath);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await suspension.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(helper.GetObserver(0).HasExited);
        Assert.Null(await helper.Client.SendAsync("read", null, default)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, helper.LaunchCount);

        helper.ReleaseOldHelper();
        await helper.WaitForExitAsync(0);
        Assert.Equal(0, helper.GetObserver(0).ExitCode);
        var response = await helper.Client.SendAsync("read", null, default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(2, helper.LaunchCount);
    }

    [Fact]
    public async Task TimedOutSuspensionAfterInputCloseWaitsForOldHelperThenRestarts()
    {
        await using var helper = new SuspensionTestProcessHarness(
            TimeSpan.FromSeconds(5), FirstHelperBehavior.WaitAfterEof);
        var description = await helper.Client.SendAsync("describe", null, default);
        Assert.True(description!.Value.GetProperty("available").GetBoolean());

        var suspension = helper.Client.SuspendForInstallationAsync(CancellationToken.None).AsTask();
        await WaitForFile(helper.EofObservedPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await suspension.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(helper.GetObserver(0).HasExited);
        Assert.Null(await helper.Client.SendAsync("read", null, default)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, helper.LaunchCount);

        helper.ReleaseOldHelper();
        await helper.WaitForExitAsync(0);
        Assert.Equal(0, helper.GetObserver(0).ExitCode);
        var response = await helper.Client.SendAsync("read", null, default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(2, helper.LaunchCount);
    }

    private static SemaphoreSlim GetInputGate(HardwareBridgeProcessClient client) =>
        (SemaphoreSlim)(typeof(HardwareBridgeProcessClient)
            .GetField("inputGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!);

    private enum FirstHelperBehavior
    {
        ServeRequests,
        WaitAfterEof
    }

    private sealed class SuspensionTestProcessHarness : IAsyncDisposable
    {
        private readonly string root;
        private readonly string placeholder;
        private readonly string finishedPath;
        private readonly string releaseOldHelperPath;
        private readonly List<Process> helpers = [];
        private readonly List<Process> observers = [];
        private readonly FirstHelperBehavior firstHelperBehavior;
        private int launches;

        public SuspensionTestProcessHarness(TimeSpan requestTimeout, FirstHelperBehavior firstHelperBehavior)
        {
            root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "rm-helper-suspend-" + Guid.NewGuid().ToString("N"))).FullName;
            var helperDirectory = Directory.CreateDirectory(Path.Combine(root,
                "Dependencies", "hardware-bridge")).FullName;
            placeholder = Path.Combine(helperDirectory, "ResourceManager.HardwareBridge.exe");
            File.WriteAllText(placeholder, "test-only factory placeholder");
            EofObservedPath = Path.Combine(root, "eof-observed");
            releaseOldHelperPath = Path.Combine(root, "release-old-helper");
            finishedPath = Path.Combine(root, "finished");
            this.firstHelperBehavior = firstHelperBehavior;

            Client = new HardwareBridgeProcessClient(
                () => placeholder,
                new HardwareBridgeProcessOptions(requestTimeout, TimeSpan.FromSeconds(1),
                    new HelperErrorOutputOptions(256, 256, 256,
                        "runtime", "version", "runtime-missing", "startup-failed")),
                startFactory: Start);
        }

        public HardwareBridgeProcessClient Client { get; }
        public string EofObservedPath { get; }
        public int LaunchCount => Volatile.Read(ref launches);

        public Process GetObserver(int index) => observers[index];

        public void ReleaseOldHelper() => File.WriteAllText(releaseOldHelperPath, "release");

        public async Task WaitForExitAsync(int index) =>
            await observers[index].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        private Process? Start(ProcessStartInfo startInfo)
        {
            _ = startInfo;
            var launch = Interlocked.Increment(ref launches);
            var script = launch == 1 && firstHelperBehavior == FirstHelperBehavior.WaitAfterEof
                ? CreateWaitAfterEofScript()
                : CreateServingScript();
            var powershell = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            powershell.ArgumentList.Add("-NoProfile");
            powershell.ArgumentList.Add("-NonInteractive");
            powershell.ArgumentList.Add("-EncodedCommand");
            powershell.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

            var helper = Process.Start(powershell)!;
            helpers.Add(helper);
            var observer = Process.GetProcessById(helper.Id);
            _ = observer.SafeHandle;
            observers.Add(observer);
            return helper;
        }

        private string CreateServingScript()
        {
            var finished = EscapePowerShell(finishedPath);
            return $$"""
                $ErrorActionPreference='Stop'
                while ($null -ne ($line=[Console]::In.ReadLine())) {
                    $request=$line | ConvertFrom-Json
                    if ($request.op -eq 'describe') {
                        [Console]::Out.WriteLine('{"available":true}')
                    } else {
                        [Console]::Out.WriteLine((@{id=$request.id;ok=$true} | ConvertTo-Json -Compress))
                    }
                    [Console]::Out.Flush()
                }
                [IO.File]::WriteAllText('{{finished}}','finished')
                """;
        }

        private string CreateWaitAfterEofScript()
        {
            var eofObserved = EscapePowerShell(EofObservedPath);
            var release = EscapePowerShell(releaseOldHelperPath);
            var finished = EscapePowerShell(finishedPath);
            return $$"""
                $ErrorActionPreference='Stop'
                $line=[Console]::In.ReadLine()
                if ($null -ne $line) {
                    $request=$line | ConvertFrom-Json
                    [Console]::Out.WriteLine('{"available":true}')
                    [Console]::Out.Flush()
                }
                $unexpected=[Console]::In.ReadLine()
                if ($null -ne $unexpected) { throw 'Unexpected command before suspension' }
                [IO.File]::WriteAllText('{{eofObserved}}','eof')
                $deadline=[DateTime]::UtcNow.AddSeconds(20)
                while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
                [IO.File]::WriteAllText('{{finished}}','finished')
                """;
        }

        private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            ReleaseOldHelper();
            Client.Dispose();
            foreach (var observer in observers)
            {
                if (!observer.HasExited)
                    await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            foreach (var observer in observers) observer.Dispose();
            foreach (var helper in helpers) helper.Dispose();
            foreach (var file in new[] { EofObservedPath, releaseOldHelperPath, finishedPath, placeholder })
                if (File.Exists(file)) File.Delete(file);
            Directory.Delete(root, recursive: true);
        }
    }
}
