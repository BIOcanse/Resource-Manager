using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [Theory]
    [InlineData(0)] [InlineData(259)]
    public async Task ActualPipeCredentialStopsAuthorizingHttpAfterOwnerExitWithRetainedHandle(int exitCode)
    {
        await using var fixture = await Fixture.StartAsync(actualSessionCredentials: true);
        using var identity = WindowsIdentity.GetCurrent();
        var name = "ResourceManager.Tests.CredentialLifetime." + Guid.NewGuid().ToString("N");
        using var pipe = WindowsLocalPipeFactory.Create(name,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        var script = $$"""
            $ErrorActionPreference='Stop'
            $pipe=[IO.Pipes.NamedPipeClientStream]::new('.', '{{name}}', [IO.Pipes.PipeDirection]::InOut)
            try {
                $pipe.Connect(5000)
                $pipe.WriteByte(1)
                $pipe.Flush()
                [Console]::In.ReadLine() | Out-Null
            } finally { $pipe.Dispose() }
            exit {{exitCode}}
            """;
        using var owner = StartCredentialTestProcess(script);
        _ = owner.SafeHandle; // Keep the exited OS object alive, not merely its numeric PID.
        var errors = owner.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(deadline.Token);
            var version = new byte[1];
            await pipe.ReadExactlyAsync(version, deadline.Token);
            Assert.Equal(1, version[0]);
            var attested = new WindowsNamedPipeCallerAttester().Attest(pipe, 1, 1);
            Assert.Equal(owner.Id, attested.ProcessId);
            Assert.Equal(identity.User.Value, attested.WindowsSid);
            Assert.Equal(owner.StartTime.ToUniversalTime().Ticks, attested.ProcessCreatedUtcTicks);
            var token = fixture.Tokens.Issue(attested); // Production OS liveness and request-owner checks.
            Assert.Equal(200, await RequestCredentialFromOwnedProcessAsync(fixture.Client.BaseAddress!, token));
            Assert.Equal(0, fixture.Administrator.Calls);
            using (var selfRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, Scope))
            {
                selfRequest.Headers.Add(LoopbackApiAccessToken.HeaderName, token);
                using var selfResponse = await fixture.Client.SendAsync(selfRequest);
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, selfResponse.StatusCode);
            }
            await owner.StandardInput.WriteLineAsync("exit");
            await owner.StandardInput.FlushAsync();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(exitCode, owner.ExitCode);
            Assert.Equal(string.Empty, await errors);
            Assert.Equal(401, await RequestCredentialFromOwnedProcessAsync(fixture.Client.BaseAddress!, token));
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Tokens.Issue(attested));
            Assert.Equal(1, fixture.Effects.ValidationCalls); // Only the live, separate client entered the protected handler.
            Assert.Null(fixture.LastRequestError);
        }
        finally
        {
            if (!owner.HasExited)
            {
                owner.StandardInput.Close();
                try { await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { owner.Kill(); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
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

    private static async Task<int> RequestCredentialFromOwnedProcessAsync(Uri url, string token)
    {
        var script = $$"""
            $ErrorActionPreference='Stop'
            $token=[Console]::In.ReadLine()
            $request=[Net.WebRequest]::Create('{{url.AbsoluteUri.TrimEnd('/')}}{{Scope}}')
            $request.Proxy=$null
            $request.Timeout=5000
            $request.Headers.Add('{{LoopbackApiAccessToken.HeaderName}}', $token)
            try { $response=$request.GetResponse() }
            catch [Net.WebException] { $response=$_.Exception.Response; if ($null -eq $response) { throw } }
            try { [Console]::Out.Write([int]$response.StatusCode) } finally { $response.Dispose() }
            """;
        using var child = StartCredentialTestProcess(script);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteLineAsync(token);
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
