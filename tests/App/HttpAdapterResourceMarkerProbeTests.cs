using System.Net;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class HttpAdapterResourceMarkerProbeTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task ConnectedOwnerIsVerifiedForEveryRegistrationBeforeSendingHttp(string address)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{address}:0");
        await using var app = builder.Build();
        var requests = 0;
        app.MapGet("/marker", () => { Interlocked.Increment(ref requests); return new { ok = true }; });
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(HttpAdapterRegistrationConnection.CreateHandler());
            var probe = new HttpAdapterResourceMarkerProbe(client);
            var request = Request(url + "/marker");
            var result = await probe.ProbeAsync(request, Caller(), default);
            Assert.Equal(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(1, requests);
            using var tree = new Tree();
            // A successful earlier request must not let another registration reuse that proof.
            result = await probe.ProbeAsync(request with { ProgramRootPaths = [tree.Root], Processes = [] }, Caller(), default);
            Assert.NotEqual(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(1, requests);

            result = await probe.ProbeAsync(request, Caller() with { WindowsSid = "S-1-5-21-other", IsAdministrator = false }, default);
            Assert.NotEqual(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(1, requests);

            result = await probe.ProbeAsync(request with { Processes = [new("missing", int.MaxValue, null)] }, Caller(), default);
            Assert.NotEqual(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(1, requests);

            result = await probe.ProbeAsync(request with { ProgramRootPaths = [Path.Combine(tree.Root, "missing")], Processes = [] }, Caller(), default);
            Assert.NotEqual(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(1, requests);

            result = await probe.ProbeAsync(request, Caller(), default);
            Assert.Equal(AdapterResourceMarkerStates.Online, result.State);
            Assert.Equal(2, requests);
        }
        finally { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [Fact]
    public async Task BareHttpSuccessWithoutOperatingSystemProofIsNotRegistrationSuccess()
    {
        using var client = new HttpClient(new SuccessHandler());
        var result = await new HttpAdapterResourceMarkerProbe(client).ProbeAsync(Request("http://localhost:12345/marker"), Caller(), default);
        Assert.Equal(AdapterResourceMarkerStates.Unsupported, result.State);
    }

    [Fact]
    public void EachRootNeedsAnObservedImageAndDeclarationMustMatchTheActualImage()
    {
        using var tree = new Tree();
        var request = Request("http://localhost:12345/marker");
        var owner = Owner();
        Assert.Throws<UnauthorizedAccessException>(() => AdapterRegistrationOwnership.Verify(
            request with { ProgramRootPaths = [.. request.ProgramRootPaths, tree.Root] }, Caller(), owner, _ => owner));
        var image = Path.Combine(tree.Root, "helper.exe");
        tree.WriteImage(image);
        var helper = owner with { Caller = owner.Caller with { ProcessId = 1234 }, ExecutablePath = image };
        var expanded = request with
        {
            ProgramRootPaths = [.. request.ProgramRootPaths, tree.Root],
            Processes = [new("helper", 1234, image)]
        };
        Assert.NotNull(AdapterRegistrationOwnership.Verify(expanded, Caller(), owner, _ => helper));
        Assert.Throws<UnauthorizedAccessException>(() => AdapterRegistrationOwnership.Verify(expanded, Caller(), owner, _ => owner));
        Assert.Throws<UnauthorizedAccessException>(() => AdapterRegistrationOwnership.Verify(expanded, Caller(), owner, _ => null));
        var wrongSid = helper with { Caller = helper.Caller with { WindowsSid = "other-user" } };
        Assert.Throws<UnauthorizedAccessException>(() => AdapterRegistrationOwnership.Verify(expanded, Caller(), owner, _ => wrongSid));
        Assert.NotNull(AdapterRegistrationOwnership.Verify(expanded, Caller() with { IsAdministrator = true }, owner, _ => wrongSid));
    }

    [Fact]
    public void ChangedCreationIdentityOrImageAfterResponseInvalidatesTheProof()
    {
        var owner = Owner();
        var proof = AdapterRegistrationOwnership.Verify(Request("http://localhost:12345/marker"), Caller(), owner, _ => owner);
        Assert.True(proof.StillOwned(_ => owner));
        Assert.False(proof.StillOwned(_ => owner with { Caller = owner.Caller with { ProcessCreatedUtcTicks = owner.Caller.ProcessCreatedUtcTicks + 1 } }));
        Assert.False(proof.StillOwned(_ => owner with { ExecutablePath = owner.ExecutablePath + ".other" }));
        Assert.False(proof.StillOwned(_ => null));
    }

    [Fact]
    public async Task ReparseRootsAndReparseExecutableAncestorsAreRejected()
    {
        using var tree = new Tree();
        var target = Path.Combine(tree.Root, "real");
        Directory.CreateDirectory(target);
        var image = Path.Combine(target, "helper.exe");
        File.WriteAllText(image, "fixture image, never executed");
        var link = Path.Combine(tree.Root, "link");
        using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, command.ExitCode);
        try
        {
            Assert.False(AdapterRegistrationOwnership.IsExistingPathWithoutReparsePoints(link, directory: true));
            Assert.False(AdapterRegistrationOwnership.IsExistingPathWithoutReparsePoints(Path.Combine(link, "helper.exe"), directory: false));
            Assert.True(AdapterRegistrationOwnership.IsExistingPathWithoutReparsePoints(image, directory: false));
            var owner = Owner() with { ExecutablePath = Path.Combine(link, "helper.exe") };
            Assert.Throws<UnauthorizedAccessException>(() => AdapterRegistrationOwnership.Verify(
                Request("http://localhost:12345/marker") with { ProgramRootPaths = [tree.Root], Processes = [] }, Caller(), owner, _ => owner));
        }
        finally { Directory.Delete(link); File.Delete(image); Directory.Delete(target); }
    }

    private static AdapterSoftwareRegistrationRequest Request(string endpoint)
        => new(AdapterRegistrationSchemaVersions.Current, "test.adapter", "test.app", "Test", null,
            [Path.GetDirectoryName(Owner().ExecutablePath)!], [new("test", Environment.ProcessId, Owner().ExecutablePath)], [],
            new(AdapterResourceMarkerTransports.LoopbackHttp, endpoint));

    private static AdapterRegistrationCaller Caller()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(identity.User!.Value, false);
    }

    private static LoopbackApiProcess Owner()
    {
        Assert.True(WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(Environment.ProcessId, out var identity));
        return identity;
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class Tree : IDisposable
    {
        private readonly List<string> files = [];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-registration-owner-" + Guid.NewGuid().ToString("N"));
        internal Tree() => Directory.CreateDirectory(Root);
        internal void WriteImage(string path) { files.Add(path); File.WriteAllText(path, "fixture image, never executed"); }
        public void Dispose() { foreach (var file in files) File.Delete(file); Directory.Delete(Root); }
    }
}
