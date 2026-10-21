using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Application.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class JsonAdapterSoftwareRegistryTests
{
    [Fact]
    public async Task LegacyResponseDoesNotGrantAdaptedStatusAndSameIdentityCanBeVerifiedAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-adapter-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Config", "adapted-software.json");
        try
        {
            var registry = new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root));
            var first = await registry.RegisterAsync(CreateRequest("first"), CreateProbe(), default);
            await registry.RegisterAsync(CreateRequest("second"), CreateProbe(), default);
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            foreach (var record in document["registrations"]!.AsArray()) record!.AsObject().Remove("ownershipVerified");
            var legacy = document.ToJsonString();
            await File.WriteAllTextAsync(path, legacy);
            registry = new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root));
            Assert.Empty(await registry.GetAllAsync(default));
            Assert.Equal(legacy, await File.ReadAllTextAsync(path));
            var unproved = CreateProbe() with { OwnershipVerified = false };
            await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(CreateRequest("first"), unproved, default));
            Assert.Equal(legacy, await File.ReadAllTextAsync(path));
            var restored = await registry.RegisterAsync(CreateRequest("first"), CreateProbe(), default);
            Assert.False(restored.Created);
            Assert.Equal(first.Registration.CreatedAt, restored.Registration.CreatedAt);
            Assert.True(restored.Registration.OwnershipVerified);
            Assert.Single(await registry.GetAllAsync(default));
            var persisted = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal(2, persisted["registrations"]!.AsArray().Count);
            Assert.Single(await new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root)).GetAllAsync(default));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var config = Path.GetDirectoryName(path)!;
            if (Directory.Exists(config)) Directory.Delete(config);
            Directory.Delete(root);
        }
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdentityCollisionCannotOverwriteAnExistingRegistration(bool collideApp)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-adapter-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root));
            var original = CreateRequest("original");
            await registry.RegisterAsync(original, CreateProbe(), default);
            var path = Path.Combine(root, "Config", "adapted-software.json");
            var before = File.ReadAllText(path);
            var conflict = collideApp ? CreateRequest("foreign") with { AppId = original.AppId }
                : CreateRequest("foreign") with { AdapterId = original.AdapterId };
            await Assert.ThrowsAsync<AdapterRegistrationConflictException>(() => registry.RegisterAsync(conflict, CreateProbe(), default));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(original.AppId, Assert.Single(await registry.GetAllAsync(default)).AppId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CrossCollisionCannotMergeTwoRegistrationsWhileSameIdentityCanUpdatePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-adapter-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root));
            var first = await registry.RegisterAsync(CreateRequest("first"), CreateProbe(), default);
            var second = await registry.RegisterAsync(CreateRequest("second"), CreateProbe(), default);
            await Assert.ThrowsAsync<AdapterRegistrationConflictException>(() => registry.RegisterAsync(
                CreateRequest("first") with { AppId = second.Registration.AppId }, CreateProbe(), default));
            Assert.Equal(2, (await registry.GetAllAsync(default)).Count);
            var updated = await registry.RegisterAsync(CreateRequest("first") with
            {
                AdapterId = "ADAPTER-FIRST", AppId = "APP-FIRST", DisplayName = "New name", ProgramRootPaths = [@"D:\Apps\NewLocation"],
                ResourceMarkerEndpoint = new(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:21000/changed")
            }, CreateProbe(), default);
            Assert.False(updated.Created);
            Assert.Equal(first.Registration.Id, updated.Registration.Id);
            Assert.Equal(first.Registration.CreatedAt, updated.Registration.CreatedAt);
            var stored = await new JsonAdapterSoftwareRegistry(new TestHostEnvironment(root)).GetAllAsync(default);
            Assert.Equal(2, stored.Count);
            Assert.Contains(stored, item => item.Id == first.Registration.Id && item.DisplayName == "New name");
            Assert.Contains(stored, item => item.Id == second.Registration.Id && item.DisplayName == second.Registration.DisplayName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
            "Ready") { OwnershipVerified = true };
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
