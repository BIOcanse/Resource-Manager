using System.Diagnostics;
using System.Text;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(259)]
    public async Task BearerKeySurvivesAnOriginProcessExitButExplicitRevocationStopsEveryCaller(int exitCode)
    {
        await using var fixture = await Fixture.StartAsync();
        var key = fixture.Key;
        using var origin = StartCredentialTestProcess($$"""
            [Console]::Out.WriteLine('ready')
            [Console]::In.ReadLine() | Out-Null
            exit {{exitCode}}
            """);
        _ = origin.SafeHandle;
        try
        {
            Assert.Equal("ready", await origin.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(200, await RequestCredentialFromOwnedProcessAsync(fixture.Client.BaseAddress!, key));
            await origin.StandardInput.WriteLineAsync("exit");
            origin.StandardInput.Close();
            await origin.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(exitCode, origin.ExitCode);
            Assert.Equal(200, await RequestCredentialFromOwnedProcessAsync(fixture.Client.BaseAddress!, key));
            var id = Assert.Single(await fixture.Keys.ListAsync(default), item => item.IsDefault).Id;
            Assert.True(await fixture.Keys.RevokeAsync(id, default));
            Assert.Equal(401, await RequestCredentialFromOwnedProcessAsync(fixture.Client.BaseAddress!, key));
            Assert.Equal(2, fixture.Effects.ValidationCalls);
            Assert.Equal(0, fixture.Administrator.Calls);
        }
        finally
        {
            if (!origin.HasExited)
            {
                origin.StandardInput.Close();
                try { await origin.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { origin.Kill(); await origin.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }

    private static Process StartCredentialTestProcess(string script)
        => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }
        })!;

    private static async Task<int> RequestCredentialFromOwnedProcessAsync(Uri url, string key)
    {
        var script = $$"""
            $ErrorActionPreference='Stop'
            $key=[Console]::In.ReadLine()
            $request=[Net.WebRequest]::Create('{{url.AbsoluteUri.TrimEnd('/')}}{{Scope}}')
            $request.Proxy=$null
            $request.Timeout=5000
            $request.Headers.Add('X-Resource-Manager-Key', $key)
            try { $response=$request.GetResponse() }
            catch [Net.WebException] { $response=$_.Exception.Response; if ($null -eq $response) { throw } }
            try { [Console]::Out.Write([int]$response.StatusCode) } finally { $response.Dispose() }
            """;
        using var child = StartCredentialTestProcess(script);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteLineAsync(key);
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(string.Empty, await errors);
        return int.Parse(await output, System.Globalization.CultureInfo.InvariantCulture);
    }
}
