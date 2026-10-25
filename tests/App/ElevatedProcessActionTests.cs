using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Localization;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class ElevatedProcessActionTests
{
    private static ProcessActionBatch Batch => new("terminate", [new(71, "132537599910000000")]);
    private static ElevatedProcessActionRequest Request => new(Guid.NewGuid().ToString("N"), 1, 2, 3, 4, 5, Batch);

    [Fact]
    public void EnvelopeRetainsOnlyOneBoundedOperationAndExactTargets()
    {
        var original = Request;
        var decoded = ElevatedProcessActionRequest.Decode(original.Encode());
        Assert.Equal(original.OperationId, decoded.OperationId);
        Assert.Equal(original.Batch.Action, decoded.Batch.Action);
        Assert.Equal(original.Batch.Targets, decoded.Batch.Targets);
        using var json = JsonDocument.Parse(Convert.FromBase64String(original.Encode()));
        Assert.False(json.RootElement.TryGetProperty("pipeName", out _));
        Assert.False(json.RootElement.TryGetProperty("accessToken", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("01")]
    [InlineData("18446744073709551615")]
    [InlineData("")]
    public void InvalidCreationIdentityIsRejected(string key)
        => Assert.Throws<InvalidDataException>(() => new ProcessActionBatch("terminate", [new(71, key)]).Validate());

    [Fact]
    public void GenericCommandsUnknownFieldsDuplicatePidsAndOversizeAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => (Batch with { Action = "shell" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Batch with { Targets = [] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Batch with { Targets = [Batch.Targets[0], Batch.Targets[0]] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Batch with
        { Targets = Enumerable.Range(1, 65).Select(pid => new ProcessActionTarget(pid, "1")).ToArray() }).Validate());
        Assert.Throws<InvalidDataException>(() => ElevatedProcessActionRequest.Decode(new('A', 30000)));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(Request.Encode()));
        var injected = json[..^1] + ",\"command\":\"anything\"}";
        Assert.Throws<JsonException>(() => ElevatedProcessActionRequest.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes(injected))));
        Assert.Equal(64, ElevatedProcessActionBroker.TryExecute([ElevatedProcessActionRequest.Argument]));
        Assert.Null(ElevatedProcessActionBroker.TryExecute([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(262145)]
    public async Task ReplyLengthIsValidatedBeforeAllocation(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevatedProcessActionRequest.ReadResultAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(71, "132537599910000000", "Succeeded", true)]
    [InlineData(71, "132537599910000001", "Succeeded", false)]
    [InlineData(72, "132537599910000000", "Succeeded", false)]
    [InlineData(71, "132537599910000000", "Invented", false)]
    public void ResultCannotSubstituteAnotherProcess(int pid, string startKey, string state, bool valid)
    {
        var payload = JsonSerializer.Serialize(new { items = new[] { new { processId = pid, processStartKey = startKey, state } } });
        if (valid) ElevatedProcessActionBroker.ValidateResult(payload, Batch);
        else Assert.Throws<InvalidDataException>(() => ElevatedProcessActionBroker.ValidateResult(payload, Batch));
    }

    [Fact]
    public async Task UtilityEntryRejectsAnUnattestedParentWithoutOpeningTheInterface()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(TestSourcePath())!, "..", ".."));
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)
            ? "Release" : "Debug";
        var executable = Path.Combine(root, "src", "UI", "Core", "bin", configuration,
            "net10.0-windows", "ResourceManager.NativeUi.exe");
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(ElevatedProcessActionRequest.Argument);
        start.ArgumentList.Add(Request.Encode());
        using var helper = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(deadline.Token);
        Assert.Equal(5, helper.ExitCode);
    }

    private static string TestSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void NativeTerminationResultsUseEveryDeclaredLanguageAndRetainBothCounts()
    {
        const string payload = "{\"action\":\"Terminate\",\"items\":[{\"state\":\"Succeeded\",\"outcome\":\"Terminated\"},{\"state\":\"Skipped\",\"outcome\":\"ProcessExited\"}]}";
        foreach (var language in NativeLanguage.SupportedIds)
        {
            var text = NativeTextCatalog.For(language);
            Assert.Contains("{0}", text.TerminateResultFormat);
            Assert.Contains("{1}", text.TerminateResultFormat);
            var actual = BackendServiceSession.SummarizeTermination(payload, text);
            Assert.Contains("1", actual); Assert.Contains("2", actual);
            if (language != "zh-CN") Assert.NotEqual("已结束 1 / 2 个进程。", actual);
        }
    }

    [Theory]
    [InlineData("Dump", "Terminated", "Succeeded")]
    [InlineData("Terminate", "DumpCreated", "Succeeded")]
    [InlineData("Terminate", "Terminated", "Failed")]
    public void NativeTerminationConsumerRejectsInconsistentFacts(string action, string outcome, string state)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { action, items = new[] { new { outcome, state } } });
        Assert.Throws<InvalidDataException>(() => BackendServiceSession.SummarizeTermination(payload, NativeTextCatalog.For("en-US")));
    }

    [Fact]
    public async Task RealLoopbackConnectionRejectsWrongCreationAndImageBeforeSendingHeaders()
    {
        using var own = Process.GetCurrentProcess();
        var expected = new BackendProcessIdentity(own.Id, own.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var mismatch in new[] { expected with { ProcessId = expected.ProcessId + 100000 },
            expected with { CreatedUtcTicks = expected.CreatedUtcTicks + 1 },
            expected with { ExecutablePath = Path.Combine(Path.GetTempPath(), "wrong.exe") } })
        {
            using var client = new HttpClient(BackendProcessConnection.CreateHandler(() => mismatch));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            request.Headers.Add("X-Resource-Manager-Token", "must-not-be-sent");
            var pending = client.SendAsync(request, deadline.Token);
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await Assert.ThrowsAsync<HttpRequestException>(() => pending);
            Assert.Equal(0, await accepted.GetStream().ReadAsync(new byte[1024], deadline.Token));
        }
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accepting = listener.AcceptTcpClientAsync(deadline.Token);
        await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var server = await accepting;
        Assert.True(BackendProcessConnection.VerifyConnectedServer(socket, expected));
        Assert.False(BackendProcessConnection.IsLoopbackHost("example.com"));

        using var verifiedClient = new HttpClient(BackendProcessConnection.CreateHandler(() => expected));
        verifiedClient.DefaultRequestHeaders.Add("X-Resource-Manager-Token", "attested-test-credential");
        var verifiedRequest = verifiedClient.GetAsync($"http://127.0.0.1:{port}/", deadline.Token);
        using var verifiedServer = await listener.AcceptTcpClientAsync(deadline.Token);
        var bytes = new byte[4096];
        var read = await verifiedServer.GetStream().ReadAsync(bytes, deadline.Token);
        Assert.Contains("attested-test-credential", Encoding.ASCII.GetString(bytes, 0, read));
        await verifiedServer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}"), deadline.Token);
        using var verifiedResponse = await verifiedRequest;
        Assert.True(verifiedResponse.IsSuccessStatusCode);
    }
}
