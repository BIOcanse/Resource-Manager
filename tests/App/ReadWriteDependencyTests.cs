using System.Net;
using ResourceManager.ReadWrite.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class ReadWriteDependencyTests
{
    [Fact]
    public async Task QueryDownloadAndInstallAreSeparateExplicitActionsUsingTheSelectedPackage()
    {
        using var transport = new Transport();
        using var client = new HttpClient(transport);
        var installs = new List<DependencyPackage>();
        var actions = new DependencyActions(client, [new("driver", () => new("driver", DependencyAvailability.Missing, "not-installed"),
            (package, token) => { installs.Add(package); return ValueTask.FromResult(new DependencyActionResult(package.DependencyId, "installed", null)); })]);
        Assert.Equal(DependencyAvailability.Missing, actions.Query("driver").Availability);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(installs);
        var directory = Path.Combine(Path.GetTempPath(), $"read-write-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "selected-package.bin");
        try
        {
            var selected = new Uri("https://packages.invalid/selected-version");
            var package = await actions.FetchAsync("driver", selected, path, CancellationToken.None);
            Assert.Equal(selected, transport.LastUri);
            Assert.Equal(Path.GetFullPath(path), package.LocalPath);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.Empty(installs);
            Assert.Equal("installed", (await actions.InstallAsync(package, CancellationToken.None)).Status);
            Assert.Equal(package, Assert.Single(installs));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task UnknownDependenciesCancelledWorkAndUnboundInstallersNeverInvokeActions()
    {
        using var transport = new Transport();
        using var client = new HttpClient(transport);
        var installs = 0;
        var actions = new DependencyActions(client, [new("selected", () => new("selected", DependencyAvailability.Unknown, "probe-unbound"), null),
            new("other", () => new("other", DependencyAvailability.Ready, null),
                (package, token) => { installs++; return ValueTask.FromResult(new DependencyActionResult(package.DependencyId, "installed", null)); })]);
        Assert.Throws<ArgumentException>(() => actions.Query("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await actions.FetchAsync("unknown", new Uri("https://packages.invalid"), "unused", CancellationToken.None));
        var unavailable = await actions.InstallAsync(new("selected", "chosen-package"), CancellationToken.None);
        Assert.Equal("unsupported", unavailable.Status);
        Assert.Equal("dependency-installer-not-bound", unavailable.Reason);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await actions.InstallAsync(new("other", "chosen-package"), cancellation.Token));
        Assert.Equal(0, installs);
        Assert.Equal(0, transport.Requests);
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++; LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
