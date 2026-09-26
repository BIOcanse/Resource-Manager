using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsLoopbackApiAdministratorReaderTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task ActualSocketIdentifiesClientProcessRatherThanBackend(string address)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{address}:0");
        await using var app = builder.Build();
        app.MapGet("/caller", (HttpContext context) =>
        {
            var found = WindowsLoopbackApiAdministratorReader.TryReadCaller(context, out var pid, out var admin);
            return new Caller(found, pid, admin);
        });
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var command = $"$ErrorActionPreference='Stop'; $r=[Net.WebRequest]::Create('{url}/caller'); $r.Proxy=$null; "
                + "$s=$r.GetResponse(); $reader=[IO.StreamReader]::new($s.GetResponseStream()); "
                + "try { [Console]::Out.Write($reader.ReadToEnd()) } finally { $reader.Dispose(); $s.Dispose() }";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            Assert.Equal(0, child.ExitCode);
            Assert.Equal("", await stderr);
            using var content = new StringContent(await stdout, Encoding.UTF8, "application/json");
            var caller = (await content.ReadFromJsonAsync<Caller>())!;
            Assert.True(caller.Found);
            Assert.Equal(child.Id, caller.ProcessId);
            Assert.NotEqual(Environment.ProcessId, caller.ProcessId);
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), caller.Administrator);
        }
        finally
        {
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void MissingConnectionDoesNotTrustClaimedAdministratorHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Is-Administrator"] = "true";
        context.Request.Headers["X-Process-Id"] = Environment.ProcessId.ToString();
        Assert.False(WindowsLoopbackApiAdministratorReader.TryReadCaller(context, out var processId, out var administrator));
        Assert.Equal(0, processId);
        Assert.False(administrator);
    }

    private sealed record Caller(bool Found, int ProcessId, bool Administrator);
}
