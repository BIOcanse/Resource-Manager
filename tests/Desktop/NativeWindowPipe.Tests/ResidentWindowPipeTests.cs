using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ResourceManager.NativeUi;

namespace ResourceManager.NativeWindow.Tests;

public sealed class ResidentWindowPipeTests
{
    [Theory]
    [InlineData("{\"type\":\"parent-wants-me-running\",\"running\":true}", "parent-wants-me-running", true, 0)]
    [InlineData("{\"type\":\"stop\"}", "parent-wants-me-running", false, 0)]
    [InlineData("{\"type\":\"backend-session\",\"ready\":true,\"generation\":123}", "backend-session", true, 123)]
    [InlineData("{\"type\":\"visibility\",\"visible\":false}", "visibility", false, 0)]
    public void ParsesParentFacts(string json, string type, bool flag, long generation)
    {
        var result = ResidentWindowPipe.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(type, result.Type); Assert.Equal(flag, result.Flag); Assert.Equal(generation, result.Generation);
    }

    [Theory]
    [InlineData("language", "language", "zh-TW")]
    [InlineData("show-window", "command", "show-passive-secondary")]
    public void ParsesWindowMessages(string type, string field, string value)
        => Assert.Equal(value, ResidentWindowPipe.Parse(Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"{field}\":\"{value}\"}}")).Value);

    [Fact]
    public async Task RealAnonymousPipeDeliversFactsAndBrokenPipeWithdrawsParentDemand()
    {
        using var parentWrite = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var childRead = new AnonymousPipeClientStream(PipeDirection.In, parentWrite.ClientSafePipeHandle);
        using var parentRead = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var childWrite = new AnonymousPipeClientStream(PipeDirection.Out, parentRead.ClientSafePipeHandle);
        using var pipe = new ResidentWindowPipe(childRead, childWrite, 10);
        var messages = Channel.CreateUnbounded<ResidentWindowMessage>();
        var receiving = pipe.RunAsync(message => { messages.Writer.TryWrite(message); return Task.CompletedTask; });
        await parentWrite.WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"parent-wants-me-running\",\"running\":true}\n{\"type\":\"backend-session\",\"ready\":true,\"generation\":2}\n"));
        Assert.True((await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Flag);
        Assert.Equal(2, (await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Generation);
        parentWrite.Dispose();
        await receiving.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await messages.Reader.ReadAsync();
        Assert.Equal("parent-wants-me-running", stopped.Type); Assert.False(stopped.Flag);
    }

    [Fact]
    public async Task OversizeInputClosesSessionAndOutputUsesExactWireNames()
    {
        using var output = new MemoryStream();
        using var pipe = new ResidentWindowPipe(new MemoryStream(new byte[ResidentWindowPipe.MaximumLineBytes]), output, 10);
        var messages = new List<ResidentWindowMessage>();
        await pipe.RunAsync(message => { messages.Add(message); return Task.CompletedTask; });
        Assert.False(Assert.Single(messages).Flag);
        await pipe.SendAsync("window-handle", "handle", "123456789");
        await pipe.SendAsync("user-close");
        await pipe.SendAsync("applyExit");
        await pipe.SendAsync("preferences-changed", "scope", "frontend");
        Assert.Equal("{\"type\":\"window-handle\",\"handle\":\"123456789\"}\n{\"type\":\"user-close\"}\n{\"type\":\"applyExit\"}\n{\"type\":\"preferences-changed\",\"scope\":\"frontend\"}\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData("--resident-pipe-read", "10")]
    [InlineData("--resident-pipe-write", "0")]
    [InlineData("--resident-pipe-write", "-1")]
    public void PartialOrInvalidHandlesDoNotFallBackToStandalone(string option, string value)
        => Assert.Throws<ArgumentException>(() => ResidentWindowPipe.FromArguments([option, value]));

    [Theory]
    [InlineData("http://example.com:19321")]
    [InlineData("http://user:secret@127.0.0.1:19321")]
    [InlineData("http://127.0.0.1:19321/api")]
    public void DevelopmentEndpointStillRequiresLoopbackRoot(string address)
        => Assert.Throws<ArgumentException>(() => ResidentWindowPipe.ApiBaseFromArguments(["--resident-api-base", address]));

    [Fact]
    public void AcceptsExplicitIsolatedEndpoint()
        => Assert.Equal("http://127.0.0.1:19321/", ResidentWindowPipe.ApiBaseFromArguments(["--resident-api-base", "http://127.0.0.1:19321"]));

    [Fact]
    public async Task StopCancelsOutboundPipeEvenWhenParentDoesNotRead()
    {
        using var parentRead = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None, 4096);
        using var childWrite = new AnonymousPipeClientStream(PipeDirection.Out, parentRead.ClientSafePipeHandle);
        await using var pipe = new ResidentWindowPipe(Stream.Null, childWrite, 10);
        var sending = pipe.SendAsync("language", "language", new string('\0', 2500));
        Assert.False(sending.IsCompleted);
        await pipe.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PipeModeRequiresCancellationPolicyBeforeOpeningHandles()
        => Assert.Throws<ArgumentException>(() => ResidentWindowPipe.FromArguments(
            ["--resident-pipe-read", "123", "--resident-pipe-write", "456"]));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("invalid")]
    public void PipeModeRejectsInvalidCancellationPolicyBeforeOpeningHandles(string value)
        => Assert.Throws<ArgumentException>(() => ResidentWindowPipe.FromArguments(
            ["--resident-pipe-read", "123", "--resident-pipe-write", "456", "--resident-cancel-poll-ms", value]));

    [Fact]
    public void NoParentMeansStartedWithoutThePipeAndNeedsNoCancellationPolicy()
        => Assert.Null(ResidentWindowPipe.FromArguments([]));

    [Fact]
    public async Task OutputQueueRejectsFullInsteadOfAccumulatingWaitingSenders()
    {
        using var output = new HeldWriteStream();
        await using var pipe = new ResidentWindowPipe(Stream.Null, output, 10);
        var sent = new List<Task> { pipe.SendAsync("language", "language", "zh-TW") };
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 64; index++) sent.Add(pipe.SendAsync("user-close"));
            var error = Assert.Throws<InvalidOperationException>(() => { _ = pipe.SendAsync("applyExit"); });
            Assert.Contains("queue is full", error.Message);
        }
        finally { output.Release.TrySetResult(); }
        await Task.WhenAll(sent).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OutputRejectsOversizeUtf8BeforeEncoding()
    {
        await using var pipe = new ResidentWindowPipe(Stream.Null, Stream.Null, 10);
        Assert.Throws<InvalidDataException>(() => { _ = pipe.SendAsync("language", "language", new string('界', 1000)); });
    }

    private sealed class HeldWriteStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            base.Write(buffer);
        }
    }
}
