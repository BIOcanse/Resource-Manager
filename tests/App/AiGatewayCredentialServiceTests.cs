using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.PublicServices.AiGateway;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.PublicServices.AiGateway;

namespace Resource_Manager_APP.Tests;

public sealed class AiGatewayCredentialServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-ai-gateway-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateValidateReloadAndRevoke_StoresOnlyDigestAndEnforcesProfile()
    {
        var database = CreateDatabase();
        var store = new SqliteAiGatewayCredentialStore(database);
        var service = new AiGatewayCredentialService(store);

        var created = await service.CreateAsync(
            new AiGatewayCredentialCreateRequest("test client", AiGatewayCompatibilityProfiles.OpenAi),
            CancellationToken.None);

        Assert.StartsWith("rm_sk_", created.ApiKey, StringComparison.Ordinal);
        Assert.True(await service.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
        Assert.False(await service.ValidateAsync(
            AiGatewayCompatibilityProfiles.Anthropic,
            created.ApiKey,
            CancellationToken.None));
        Assert.Equal(created.Credential, Assert.Single(await service.ListAsync(CancellationToken.None)));

        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT typeof(secret_hash), length(secret_hash) FROM ai_gateway_credentials;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("blob", reader.GetString(0));
            Assert.Equal(32, reader.GetInt32(1));
        }

        var reloaded = new AiGatewayCredentialService(new SqliteAiGatewayCredentialStore(database));
        Assert.True(await reloaded.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
        Assert.True(await reloaded.RevokeAsync(created.Credential.Id, CancellationToken.None));
        Assert.False(await reloaded.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private ResourceManagerDatabase CreateDatabase()
    {
        var contentRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new ResourceManagerDatabase(new TestHostEnvironment(contentRoot));
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
