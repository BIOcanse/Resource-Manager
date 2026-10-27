using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class ElevatedBackendRequestTests
{
    private static ElevatedBackendInvocation Invocation => new(Guid.NewGuid().ToString("N"), 1, 2, 3, 4);
    private static ElevatedBackendMutation Mutation => new("PUT", "/api/startup-modules/software/manual%3A%E8%BD%AF%E4%BB%B6",
        "{\"softwareId\":\"manual:软件\",\"moduleIds\":[\"renderer\"]}");

    [Fact]
    public void CommandLineContainsOnlyAttestationMetadataAndNoMutationOrKey()
    {
        var invocation = Invocation;
        var encoded = ElevatedBackendRequestWire.EncodeInvocation(invocation);
        Assert.Equal(invocation, ElevatedBackendRequestWire.DecodeInvocation(encoded));
        using var json = JsonDocument.Parse(Convert.FromBase64String(encoded));
        Assert.Equal(5, json.RootElement.EnumerateObject().Count());
        foreach (var field in new[] { "method", "path", "body", "key", "apiKey", "pipeName" })
            Assert.False(json.RootElement.TryGetProperty(field, out _));
        var injected = Encoding.UTF8.GetBytes(json.RootElement.GetRawText()[..^1] + ",\"body\":\"{}\"}");
        Assert.Throws<JsonException>(() => ElevatedBackendRequestWire.DecodeInvocation(Convert.ToBase64String(injected)));
        Assert.Throws<InvalidDataException>(() => ElevatedBackendRequestWire.DecodeInvocation(new string('a', 4097)));
    }

    [Fact]
    public async Task AttestedPipePayloadRoundTripsMutationAndRealHttpStatusWithoutReflection()
    {
        using var request = new MemoryStream();
        await ElevatedBackendRequestWire.WriteMutationAsync(request, Mutation, default);
        request.Position = 0;
        Assert.Equal(Mutation, await ElevatedBackendRequestWire.ReadMutationAsync(request, default));
        var expected = new ElevatedBackendResponse(403, "{\"error\":\"elevated-administrator-required\"}");
        using var response = new MemoryStream();
        await ElevatedBackendRequestWire.WriteResponseAsync(response, expected, default);
        response.Position = 0;
        Assert.Equal(expected, await ElevatedBackendRequestWire.ReadResponseAsync(response, default));
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, ElevatedBackendRequestWire.MaximumFrameBytes + 1);
        using var oversized = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevatedBackendRequestWire.ReadMutationAsync(oversized, default));
    }

    [Theory]
    [InlineData("GET", "/api/settings/app")]
    [InlineData("PUT", "https://other.test/api/settings/app")]
    [InlineData("PUT", "//other.test/api/settings/app")]
    [InlineData("PUT", "/api/../outside")]
    [InlineData("PUT", "/api/%2e%2e/outside")]
    [InlineData("PUT", "/api/value%2foutside")]
    [InlineData("PUT", "/api/value%5coutside")]
    [InlineData("PUT", "/api/settings?override=true")]
    [InlineData("PUT", "/api/settings#fragment")]
    [InlineData("PUT", "/api/%xx")]
    public void ReadAndNonApiOrAmbiguousAddressesCannotTriggerElevation(string method, string path)
        => Assert.Throws<InvalidDataException>(() => new ElevatedBackendMutation(method, path, "{}").Validate());

    [Fact]
    public void MutationRequiresBoundedValidJsonButAllowsEncodedSoftwareIdentity()
    {
        Mutation.Validate();
        Assert.ThrowsAny<JsonException>(() => (Mutation with { Body = "not-json" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Mutation with { Body = new string('a', ElevatedBackendRequestWire.MaximumBodyBytes + 1) }).Validate());
    }

    [Fact]
    public async Task AlreadyCanceledMutationDoesNotCreateAnElevationProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var launches = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ElevatedProcessActionBroker.ExecuteBackendRequestAsync(
            Mutation, new BackendProcessIdentity(123, 456, Path.GetFullPath("owned-backend.exe")), cancellation.Token,
            _ => { launches++; throw new InvalidOperationException("Canceled work must never invoke the launcher."); }));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task ExplicitMutationUsesNoKeyAndPreservesBackendDenialInsteadOfFakingSaveSuccess()
    {
        using var handler = new MutationHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal(9321, request.RequestUri.Port);
            Assert.Equal(Mutation.Path, request.RequestUri.PathAndQuery);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains(TrustedAccessHeaders.KeyHeader));
            Assert.Equal(Mutation.Body, await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"error\":\"elevated-administrator-required\"}", Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9321") };
        var result = await ElevatedProcessActionBroker.SendBackendMutationAsync(client, Mutation, default);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("elevated-administrator-required", result.Body);
    }

    [Fact]
    public async Task ResponseSizeAndContentTypeAreBoundedBeforeReturningToThePage()
    {
        using var tooLargeHandler = new MutationHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[ElevatedBackendRequestWire.MaximumResponseBytes + 1]) }));
        using var tooLarge = new HttpClient(tooLargeHandler) { BaseAddress = new Uri("http://127.0.0.1:9321") };
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevatedProcessActionBroker.SendBackendMutationAsync(tooLarge, Mutation, default));
        using var textHandler = new MutationHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("unexpected text") }));
        using var text = new HttpClient(textHandler) { BaseAddress = new Uri("http://127.0.0.1:9321") };
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevatedProcessActionBroker.SendBackendMutationAsync(text, Mutation, default));
        using var emptyHandler = new MutationHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var empty = new HttpClient(emptyHandler) { BaseAddress = new Uri("http://127.0.0.1:9321") };
        Assert.Equal(new ElevatedBackendResponse(204, ""),
            await ElevatedProcessActionBroker.SendBackendMutationAsync(empty, Mutation, default));
    }

    [Fact]
    public async Task GenericUtilityEntryRejectsUnattestedParentWithoutOpeningGuardOrInterface()
    {
        var executable = NativeUiTestExecutableLocator.GetPath();
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(ElevatedBackendInvocation.Argument);
        start.ArgumentList.Add(ElevatedBackendRequestWire.EncodeInvocation(Invocation));
        using var helper = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(deadline.Token);
        Assert.Equal(5, helper.ExitCode);
    }

    private sealed class MutationHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
