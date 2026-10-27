using System.Net;
using System.Net.Http;
using ResourceManager.NativeUi;

namespace ResourceManager.NativeWindow.Tests;

public sealed class WindowBackendTransportTests
{
    [Fact]
    public async Task WindowPreparationDoesNotProbeOrStartPeriodicHealthTraffic()
    {
        var requests = 0;
        var keyReads = 0;
        await using var session = new BackendServiceSession("http://127.0.0.1:19321/",
            () => { keyReads++; return "test-key"; },
            () => new BackendProcessIdentity(123, 456, "isolated-backend.exe"),
            handlerFactory: () => new Handler(request =>
            {
                requests++;
                Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Resource-Manager-Key")));
            }));
        session.PrepareWindowTransport();
        Assert.Equal(0, requests);
        Assert.Equal(0, keyReads);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:19321/api/test");
        using var response = await session.SendFrontendRequestAsync(request, CancellationToken.None);
        Assert.Equal(1, requests);
        Assert.Equal(1, keyReads);
    }

    [Fact]
    public async Task MissingIdentityNeverReadsCredentialOrSendsRequest()
    {
        var keyReads = 0;
        var requests = 0;
        await using var session = new BackendServiceSession("http://127.0.0.1:19321/",
            () => { keyReads++; return "test-key"; }, () => null,
            handlerFactory: () => new Handler(_ => requests++));
        Assert.Throws<InvalidOperationException>(session.PrepareWindowTransport);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:19321/api/test");
        Assert.Throws<InvalidOperationException>(() => { _ = session.SendFrontendRequestAsync(request, CancellationToken.None); });
        Assert.Equal(0, requests);
        Assert.Equal(0, keyReads);
    }

    [Fact]
    public async Task StaticDocumentsUsePinnedTransportWithoutReadingBusinessCredential()
    {
        var requests = 0;
        await using var session = new BackendServiceSession("http://127.0.0.1:19321/",
            () => throw new InvalidOperationException("Static documents do not use the API key."),
            () => new BackendProcessIdentity(123, 456, "isolated-backend.exe"),
            handlerFactory: () => new Handler(request =>
            {
                requests++;
                Assert.False(request.Headers.Contains("X-Resource-Manager-Key"));
            }));
        session.PrepareWindowTransport();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:19321/index.html");
        using var response = await session.SendFrontendRequestAsync(request, CancellationToken.None);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:19322/api/test")]
    [InlineData("https://127.0.0.1:19321/api/test")]
    [InlineData("http://user:secret@127.0.0.1:19321/api/test")]
    public async Task ForeignEndpointIsRejectedBeforeCredentialReadOrRequest(string address)
    {
        await using var session = new BackendServiceSession("http://127.0.0.1:19321/",
            () => throw new InvalidOperationException("Foreign request must not read a key."),
            () => new BackendProcessIdentity(123, 456, "isolated-backend.exe"),
            handlerFactory: () => new Handler(_ => throw new InvalidOperationException("Foreign request must not be sent.")));
        session.PrepareWindowTransport();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        Assert.Throws<UnauthorizedAccessException>(() => { _ = session.SendFrontendRequestAsync(request, CancellationToken.None); });
    }

    private sealed class Handler(Action<HttpRequestMessage> observe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
