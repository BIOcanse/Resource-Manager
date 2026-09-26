using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class JsonAdapterSoftwareRegistryTests
{
    [Fact]
    public async Task ReadsUseTheInMemorySnapshotAndMutationsPersistIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"adapter-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry =
                new JsonAdapterSoftwareRegistry(
                    new TestHostEnvironment(root));
            var first = await registry.RegisterAsync(
                CreateRequest("first"),
                CreateProbe(),
                CancellationToken.None);
            var storagePath = Path.Combine(
                root,
                "Config",
                "adapted-software.json");
            Assert.True(File.Exists(storagePath));

            var initialSnapshot =
                await registry.GetAllAsync(CancellationToken.None);
            Assert.Single(initialSnapshot);
            File.Delete(storagePath);

            var cachedSnapshot =
                await registry.GetAllAsync(CancellationToken.None);
            Assert.Same(initialSnapshot, cachedSnapshot);
            Assert.Equal(first.Registration.Id, cachedSnapshot[0].Id);
            Assert.False(File.Exists(storagePath));

            _ = await registry.RegisterAsync(
                CreateRequest("second"),
                CreateProbe(),
                CancellationToken.None);
            Assert.True(File.Exists(storagePath));

            var restarted =
                new JsonAdapterSoftwareRegistry(
                    new TestHostEnvironment(root));
            var persisted =
                await restarted.GetAllAsync(CancellationToken.None);
            Assert.Equal(2, persisted.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AdapterSoftwareRegistrationRequest CreateRequest(
        string id)
    {
        return new AdapterSoftwareRegistrationRequest(
            AdapterRegistrationSchemaVersions.Current,
            $"adapter-{id}",
            $"app-{id}",
            $"Adapter {id}",
            null,
            [$@"D:\Apps\{id}"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                $"http://127.0.0.1:1900{id.Length}"));
    }

    private static AdapterResourceMarkerProbeResult CreateProbe()
    {
        return new AdapterResourceMarkerProbeResult(
            AdapterResourceMarkerStates.Online,
            DateTimeOffset.UtcNow,
            200,
            "Ready");
    }

    private sealed class TestHostEnvironment(
        string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } =
            Environments.Development;

        public string ApplicationName { get; set; } =
            "ResourceManager.Tests";

        public string ContentRootPath { get; set; } =
            contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
