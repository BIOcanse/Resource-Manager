using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [Fact]
    public async Task AccessRequestContentLengthAboveLimitIsRejectedBeforeRequestCreation()
    {
        await using var fixture = await Fixture.StartAsync();
        var body = Encoding.UTF8.GetBytes(
            "{\"name\":\"" + new string('a', TrustedAccessApiContract.AccessRequestMaximumBodyBytes)
            + "\",\"requestedScopes\":[\"read\"]}");
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await fixture.Client.PostAsync(TrustedAccessApiContract.AccessRequestsPath, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await AssertNoPendingRequestsAsync(fixture);
    }

    [Fact]
    public async Task ChunkedAccessRequestAboveLimitIsRejectedBeforeRequestCreation()
    {
        await using var fixture = await Fixture.StartAsync();
        var body = Encoding.UTF8.GetBytes(
            "{\"name\":\"" + new string('a', TrustedAccessApiContract.AccessRequestMaximumBodyBytes)
            + "\",\"requestedScopes\":[\"read\"]}");
        using var request = new HttpRequestMessage(HttpMethod.Post, TrustedAccessApiContract.AccessRequestsPath)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ChunkedJsonContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await AssertNoPendingRequestsAsync(fixture);
    }

    [Fact]
    public async Task SubscriptionEnvelopeCannotSmuggleASecondSelectorBeyondItsKeyScope()
    {
        await using var fixture = await Fixture.StartAsync();
        var metricsKey = await fixture.Keys.CreateAsync(
            new AccessKeyCreateRequest("Metrics-only client", AccessKeyPurposes.Control,
                [AccessKeyScopes.MetricsRead]),
            CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = TrustedAccessApiContract.Version,
                subscriptions = new[]
                {
                    new { id = "metrics", path = "/api/metrics/subscribe" },
                    new { id = "processes", path = "/api/resource-monitor/subscribe" }
                }
            })
        };
        request.Headers.Add(TrustedAccessHeaders.KeyHeader, metricsKey.ApiKey);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("access-scope-required", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Fact]
    public async Task ResourceFactsSnapshotRejectsOrdinaryReadKeyAndAcceptsProcessReadKey()
    {
        await using var fixture = await Fixture.StartAsync();
        var readKey = await fixture.Keys.CreateAsync(new("ordinary read", AccessKeyPurposes.Control,
            [AccessKeyScopes.Read]), CancellationToken.None);
        var processKey = await fixture.Keys.CreateAsync(new("process read", AccessKeyPurposes.Control,
            [AccessKeyScopes.ProcessesRead]), CancellationToken.None);
        foreach (var key in new[] { readKey, processKey })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/resource-facts/snapshot");
            request.Headers.Add(TrustedAccessHeaders.KeyHeader, key.ApiKey);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(key == readKey ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(0, fixture.Sampler.Captures);
    }

    [Fact]
    public async Task UnknownSelectorIsRejectedByIdWhileResourceFactsSubscriptionStillPublishes()
    {
        await using var fixture = await Fixture.StartAsync();
        var processKey = await fixture.Keys.CreateAsync(new("process read", AccessKeyPurposes.Control,
            [AccessKeyScopes.ProcessesRead]), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "unknown", path = "/api/not-registered/subscribe" },
                new { id = "facts", path = "/api/resource-facts/subscribe" } } })
        };
        request.Headers.Add(TrustedAccessHeaders.KeyHeader, processKey.ApiKey);
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        using var rejection = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert.Equal("unknown", rejection.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(400, rejection.RootElement.GetProperty("value").GetProperty("statusCode").GetInt32());
        Assert.Equal("unsupported-subscription-selector", rejection.RootElement.GetProperty("value").GetProperty("error").GetString());
        using var value = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert.Equal("facts", value.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(1, value.RootElement.GetProperty("value").GetProperty("version").GetInt32());
        Assert.Empty(value.RootElement.GetProperty("value").GetProperty("processes").EnumerateArray());
        Assert.Equal(0, fixture.Sampler.Captures);
        await deadline.CancelAsync();
    }

    [Fact]
    public async Task LocalKeylessRequestIsNarrowlyApprovedAndDeliveredOnceThroughHttp()
    {
        await using var fixture = await Fixture.StartAsync();
        PublishRequestLifetime(fixture, 300_000);

        using var streamDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = TrustedAccessApiContract.Version,
                subscriptions = new[]
                {
                    new { id = "access-requests", path = TrustedAccessApiContract.AccessRequestsSubscriptionPath }
                }
            })
        };
        streamRequest.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var streamResponse = await fixture.Client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            streamDeadline.Token);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        using var streamReader = new StreamReader(
            await streamResponse.Content.ReadAsStreamAsync(streamDeadline.Token));

        using var initialSnapshot = JsonDocument.Parse(
            (await streamReader.ReadLineAsync(streamDeadline.Token))!);
        Assert.Equal("access-requests",
            initialSnapshot.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(0, initialSnapshot.RootElement.GetProperty("value").GetArrayLength());

        using var createResponse = await fixture.Client.PostAsJsonAsync(
            TrustedAccessApiContract.AccessRequestsPath,
            new
            {
                name = "Local helper",
                requestedScopes = new[] { AccessKeyScopes.Read, AccessKeyScopes.MetricsRead }
            },
            streamDeadline.Token);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        Assert.Equal("no-store", createResponse.Headers.CacheControl?.ToString());
        using var createDocument = JsonDocument.Parse(
            await createResponse.Content.ReadAsStringAsync(streamDeadline.Token));
        var requestId = createDocument.RootElement.GetProperty("requestId").GetString()!;
        var deliverySecret = createDocument.RootElement.GetProperty("deliverySecret").GetString()!;
        Assert.Equal(64, requestId.Length);
        Assert.Equal(64, deliverySecret.Length);

        using var pendingSnapshot = JsonDocument.Parse(
            (await streamReader.ReadLineAsync(streamDeadline.Token))!);
        var pending = Assert.Single(pendingSnapshot.RootElement.GetProperty("value").EnumerateArray());
        Assert.Equal(requestId, pending.GetProperty("requestId").GetString());
        Assert.Equal("Local helper", pending.GetProperty("name").GetString());
        Assert.True(pending.GetProperty("observedProcessId").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(pending.GetProperty("observedExecutablePath").GetString()));
        Assert.False(pending.TryGetProperty("deliverySecret", out _));

        using var waitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var waitRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/wait")
        {
            Content = JsonContent.Create(new { deliverySecret })
        };
        var waitTask = fixture.Client.SendAsync(
            waitRequest,
            HttpCompletionOption.ResponseHeadersRead,
            waitDeadline.Token);
        await WaitForRequestCountAsync(fixture, expected: 3, waitDeadline.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(75), waitDeadline.Token);
        Assert.False(waitTask.IsCompleted);

        using var approveRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/approve")
        {
            Content = JsonContent.Create(new { scopes = new[] { AccessKeyScopes.Read } })
        };
        approveRequest.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var approveResponse = await fixture.Client.SendAsync(approveRequest, waitDeadline.Token);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approveBody = await approveResponse.Content.ReadAsStringAsync(waitDeadline.Token);
        using var approveDocument = JsonDocument.Parse(approveBody);
        Assert.Equal("approved", approveDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(new[] { AccessKeyScopes.Read },
            approveDocument.RootElement.GetProperty("key").GetProperty("scopes")
                .EnumerateArray().Select(static value => value.GetString()!).ToArray());
        Assert.False(approveDocument.RootElement.TryGetProperty("apiKey", out _));

        using var waitResponse = await waitTask.WaitAsync(waitDeadline.Token);
        Assert.Equal(HttpStatusCode.OK, waitResponse.StatusCode);
        Assert.Equal("no-store", waitResponse.Headers.CacheControl?.ToString());
        using var deliveredDocument = JsonDocument.Parse(
            await waitResponse.Content.ReadAsStringAsync(waitDeadline.Token));
        Assert.Equal("approved", deliveredDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("rm_sk_", deliveredDocument.RootElement.GetProperty("apiKey").GetString()![..6]);
        Assert.Equal(new[] { AccessKeyScopes.Read },
            deliveredDocument.RootElement.GetProperty("key").GetProperty("scopes")
                .EnumerateArray().Select(static value => value.GetString()!).ToArray());

        using var secondWait = await fixture.Client.PostAsJsonAsync(
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/wait",
            new { deliverySecret },
            waitDeadline.Token);
        Assert.Equal(HttpStatusCode.OK, secondWait.StatusCode);
        using var deliveredAgain = JsonDocument.Parse(
            await secondWait.Content.ReadAsStringAsync(waitDeadline.Token));
        Assert.Equal("delivered", deliveredAgain.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, deliveredAgain.RootElement.GetProperty("apiKey").ValueKind);

        await streamDeadline.CancelAsync();
    }

    [Fact]
    public async Task HttpApproverCannotGrantUnheldScopeAndDenialIsTypedForRequester()
    {
        await using var fixture = await Fixture.StartAsync();
        PublishRequestLifetime(fixture, 300_000);

        using var createResponse = await fixture.Client.PostAsJsonAsync(
            TrustedAccessApiContract.AccessRequestsPath,
            new
            {
                name = "Elevated helper",
                requestedScopes = new[] { AccessKeyScopes.MachineWrite }
            });
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var requestId = createDocument.RootElement.GetProperty("requestId").GetString()!;
        var deliverySecret = createDocument.RootElement.GetProperty("deliverySecret").GetString()!;

        using var approveRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/approve")
        {
            Content = JsonContent.Create(new { scopes = new[] { AccessKeyScopes.MachineWrite } })
        };
        approveRequest.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var escalation = await fixture.Client.SendAsync(approveRequest);
        Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
        using var escalationDocument = JsonDocument.Parse(await escalation.Content.ReadAsStringAsync());
        Assert.Equal("approved-scope-exceeds-grant",
            escalationDocument.RootElement.GetProperty("error").GetString());

        using var denyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/deny")
        {
            Content = JsonContent.Create(new { reasonCode = "not-approved" })
        };
        denyRequest.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var denied = await fixture.Client.SendAsync(denyRequest);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        using var deniedDocument = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("denied", deniedDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("not-approved", deniedDocument.RootElement.GetProperty("denialReason").GetString());

        using var waitDenied = await fixture.Client.PostAsJsonAsync(
            $"{TrustedAccessApiContract.AccessRequestsPath}/{requestId}/wait",
            new { deliverySecret });
        Assert.Equal(HttpStatusCode.OK, waitDenied.StatusCode);
        using var waitDeniedDocument = JsonDocument.Parse(await waitDenied.Content.ReadAsStringAsync());
        Assert.Equal("denied", waitDeniedDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("not-approved", waitDeniedDocument.RootElement.GetProperty("denialReason").GetString());
    }

    private static void PublishRequestLifetime(Fixture fixture, int milliseconds)
        => fixture.Runtime.Publish(fixture.Runtime.Current with
        {
            HostManager = CompiledHostManagerPlan.Unpublished with
            {
                TrustedAccessRequestLifetimeMilliseconds = milliseconds
            }
        });

    private static async Task WaitForRequestCountAsync(
        Fixture fixture,
        int expected,
        CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref fixture.HttpRequests) < expected)
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
    }

    private static async Task AssertNoPendingRequestsAsync(Fixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pending = fixture.App.Services
            .GetRequiredService<ITrustedAccessRequestOwner>()
            .SubscribePendingAsync(deadline.Token)
            .GetAsyncEnumerator(deadline.Token);
        Assert.True(await pending.MoveNextAsync());
        Assert.Empty(pending.Current);
    }

    private sealed class ChunkedJsonContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
