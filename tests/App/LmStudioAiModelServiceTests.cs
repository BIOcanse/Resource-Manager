using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.PublicServices.AiModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class LmStudioAiModelServiceTests
{
    [Fact]
    public async Task ListModels_MapsProviderPayloadToStableCatalog()
    {
        const string payload =
            """
            {
              "models": [{
                "type": "llm",
                "publisher": "google",
                "key": "google/gemma-4-e2b",
                "display_name": "Gemma 4 E2B",
                "architecture": "gemma4",
                "format": "gguf",
                "quantization": { "name": "Q4_K_M", "bits_per_weight": 4 },
                "size_bytes": 4414806160,
                "params_string": "4.6B",
                "max_context_length": 131072,
                "capabilities": { "vision": true, "trained_for_tool_use": true },
                "loaded_instances": []
              }]
            }
            """;
        var service = CreateService(_ => Json(payload));

        var model = Assert.Single(
            await service.ListModelsIfRunningAsync(CancellationToken.None));

        Assert.Equal("google/gemma-4-e2b", model.Key);
        Assert.Equal("Q4_K_M", model.Quantization);
        Assert.Equal(4, model.BitsPerWeight);
        Assert.Equal(4_414_806_160, model.SizeBytes);
        Assert.Contains("vision", model.Capabilities);
        Assert.Contains("trained_for_tool_use", model.Capabilities);
    }

    [Fact]
    public async Task RuntimeStatus_ProbesWithoutStartingTheCli()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.False(status.ServerRunning);
        Assert.Equal("lm-studio", status.Provider);
        Assert.Equal("http://127.0.0.1:1234", status.Endpoint);
    }

    [Fact]
    public async Task CatalogAcquisitionDoesNotStartAnUnavailableProvider()
    {
        var requestCount = 0;
        var service = CreateService(_ =>
        {
            requestCount++;
            throw new HttpRequestException("offline");
        });

        await Assert.ThrowsAsync<AiModelRuntimeUnavailableException>(
            () => service.ListModelsIfRunningAsync(CancellationToken.None));

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ForwardAsync_StripsClientCredentialsBeforeCallingProvider()
    {
        var forwarded = false;
        var leakedAuthorization = false;
        var leakedAnthropicKey = false;
        var leakedResourceManagerToken = false;
        var service = CreateService(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/models")
            {
                return Json("{\"models\":[]}");
            }

            forwarded = request.RequestUri?.AbsolutePath == "/v1/messages";
            leakedAuthorization = request.Headers.Authorization is not null;
            leakedAnthropicKey = request.Headers.Contains("x-api-key");
            leakedResourceManagerToken = request.Headers.Contains("X-Resource-Manager-Token");
            return Json("{}");
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentLength = 2;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        context.Request.Headers.Authorization = "Bearer rm_sk_client";
        context.Request.Headers["x-api-key"] = "rm_sk_anthropic";
        context.Request.Headers["X-Resource-Manager-Token"] = "internal";
        context.Response.Body = new MemoryStream();

        await service.ForwardAsync(context, "/v1/messages", CancellationToken.None);

        Assert.True(forwarded);
        Assert.False(leakedAuthorization);
        Assert.False(leakedAnthropicKey);
        Assert.False(leakedResourceManagerToken);
    }

    private static LmStudioAiModelService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        return new LmStudioAiModelService(
            new HttpClient(new StubHandler(responseFactory)) { Timeout = Timeout.InfiniteTimeSpan },
            new StaticSettingsStore(AppSettingsDefaults.Create()),
            NullLogger<LmStudioAiModelService>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class StaticSettingsStore(AppSettings settings) : IAppSettingsStore
    {
        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(HostManagerTestPlanFactory.CreateSettingsInput(settings.Performance) with
            {
                Settings = settings,
                StoragePath = "memory"
            });

        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(
            CancellationToken cancellationToken) => LoadAsync(cancellationToken);

        public Task<AppSettingsUpdateResult> SaveAsync(
            AppSettings next,
            CancellationToken cancellationToken) =>
            Task.FromResult(HostManagerTestPlanFactory.CreateSettingsInput(next.Performance) with
            {
                Settings = next,
                StoragePath = "memory"
            });

        public async Task<AppSettingsUpdateResult> SaveValidatedAsync(
            AppSettings next,
            Func<AppSettingsUpdateResult, CancellationToken, Task> validateBeforeCommit,
            CancellationToken cancellationToken)
        {
            var candidate = await SaveAsync(next, cancellationToken);
            await validateBeforeCommit(candidate, cancellationToken);
            return candidate;
        }
    }
}
