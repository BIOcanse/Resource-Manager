using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Infrastructure.PublicServices;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerPublicServiceCoordinatorOwnerTests
{
    [Fact]
    public async Task PublicServiceDisabledInferenceReturnsEmpty404FromNativeAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        await LoopbackApiAccessTests.WithPublicAiModelClientAsync(fixture.Owner, fixture.Owner, async client =>
        {
            using var response = await client.PostAsJsonAsync(
                "/api/public/v1/ai/compat/openai/v1/chat/completions", new { model = "community/open-model" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        });
        Assert.False(fixture.Owner.HasPendingTasks);
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
        Assert.Equal(0U, fixture.Lifetimes.LastCreated.ReadSnapshot().RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicAiModelMutationDisabledAfterHttpAdmissionReturnsEmpty404(bool unload)
    {
        await using var fixture = await Fixture.CreateAsync();
        await WaitForCatalogAsync(fixture.Owner);
        var policy = new DisableAfterAdmissionPolicy(fixture.Owner,
            () => fixture.PublishPlan(_ => { }, enabled: false));
        await LoopbackApiAccessTests.WithPublicAiModelClientAsync(fixture.Owner, policy, async client =>
        {
            using var response = unload
                ? await client.PostAsJsonAsync("/api/public/v1/ai/models/unload", new AiModelUnloadRequest("open-model-instance"))
                : await client.PostAsJsonAsync("/api/public/v1/ai/models/load", NewLoadRequest());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        });
        Assert.True(policy.AdmittedBeforeDisable);
        Assert.Equal(0, fixture.Provider.LoadCallCount);
        Assert.Equal(0, fixture.Provider.UnloadCallCount);
        Assert.Equal(0U, fixture.Lifetimes.LastCreated.ReadSnapshot().QueuedTaskCount);
        Assert.Equal(0U, fixture.Lifetimes.LastCreated.ReadSnapshot().RunningTaskCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task PublicAiModelHttpMutationWhileDisabledReturnsEmpty404WithoutProviderCalls(
        bool unload, bool warmCatalog, bool nativeHttpAdmission)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: warmCatalog);
        if (warmCatalog) await WaitForCatalogAsync(fixture.Owner);
        var retainedRequest = warmCatalog ? await AdmitIndexAsync(fixture) : null;
        if (warmCatalog) fixture.PublishPlan(_ => { }, enabled: false);
        if (warmCatalog) Assert.NotEqual(0UL, fixture.Lifetimes.LastCreated.ReadSnapshot().ModelGeneration);
        var catalogCalls = fixture.Provider.CatalogCallCount;
        try
        {
            await LoopbackApiAccessTests.WithPublicAiModelClientAsync(
                fixture.Owner, nativeHttpAdmission ? fixture.Owner : null, async client =>
                {
                    using var response = unload
                        ? await client.PostAsJsonAsync("/api/public/v1/ai/models/unload", new AiModelUnloadRequest("open-model-instance"))
                        : await client.PostAsJsonAsync("/api/public/v1/ai/models/load", NewLoadRequest());
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                });
            Assert.Equal(catalogCalls, fixture.Provider.CatalogCallCount);
            Assert.Equal(0, fixture.Provider.LoadCallCount);
            Assert.Equal(0, fixture.Provider.UnloadCallCount);
            Assert.Equal(0U, fixture.Lifetimes.LastCreated.ReadSnapshot().QueuedTaskCount);
            Assert.Equal(0U, fixture.Lifetimes.LastCreated.ReadSnapshot().RunningTaskCount);
            if (!warmCatalog) Assert.False(fixture.Owner.HasPendingTasks);
        }
        finally
        {
            if (retainedRequest is not null)
                await fixture.Owner.CompleteAsync(retainedRequest.CompletionHandle, default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicAiModelHttpMutationWhileEnabledStillSucceeds(bool unload)
    {
        await using var fixture = await Fixture.CreateAsync();
        await WaitForCatalogAsync(fixture.Owner);
        await LoopbackApiAccessTests.WithPublicAiModelClientAsync(fixture.Owner, fixture.Owner, async client =>
        {
            using var response = unload
                ? await client.PostAsJsonAsync("/api/public/v1/ai/models/unload", new AiModelUnloadRequest("open-model-instance"))
                : await client.PostAsJsonAsync("/api/public/v1/ai/models/load", NewLoadRequest());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (unload)
                Assert.Equal("open-model-instance", (await response.Content.ReadFromJsonAsync<AiModelUnloadResult>())!.InstanceId);
            else
                Assert.Equal("community/open-model", (await response.Content.ReadFromJsonAsync<AiModelLoadResult>())!.InstanceId);
        });
        Assert.Equal(unload ? 0 : 1, fixture.Provider.LoadCallCount);
        Assert.Equal(unload ? 1 : 0, fixture.Provider.UnloadCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicAiModelDirectMutationWhileDisabledDoesNotDemandColdCatalog(bool unload)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        await Assert.ThrowsAsync<AiModelServiceDisabledException>(async () =>
        {
            if (unload) await fixture.Owner.UnloadAsync(new AiModelUnloadRequest("open-model-instance"), default);
            else await fixture.Owner.LoadAsync(NewLoadRequest(), default);
        });
        Assert.Equal(0, fixture.Provider.CatalogCallCount);
        Assert.Equal(0, fixture.Provider.LoadCallCount);
        Assert.Equal(0, fixture.Provider.UnloadCallCount);
        Assert.False(fixture.Owner.HasPendingTasks);
    }

    private sealed class DisableAfterAdmissionPolicy(
        HostManagerPublicServiceCoordinatorOwner owner,
        Action disable) : ILocalPublicServiceAccessPolicy
    {
        internal bool AdmittedBeforeDisable { get; private set; }

        public async Task<LocalPublicServiceAccessDecision> EvaluateAsync(
            HttpContext context,
            CancellationToken cancellationToken)
        {
            var decision = await owner.EvaluateAsync(context, cancellationToken);
            Assert.True(decision.Allowed);
            AdmittedBeforeDisable = true;
            disable();
            return decision;
        }

        public ValueTask CompleteAsync(ulong completionHandle, CancellationToken cancellationToken)
            => owner.CompleteAsync(completionHandle, cancellationToken);
    }
}
