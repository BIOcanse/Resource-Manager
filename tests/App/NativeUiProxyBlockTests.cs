using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiProxyBlockTests
{
    [Fact]
    public async Task FailedStartWaitsForExplicitRetryThenRuns()
    {
        var attempts = 0;
        var failed = Signal();
        var running = Signal();
        using var stopping = new CancellationTokenSource();
        await using var proxy = Proxy(_ => ++attempts == 1
            ? Task.FromException(new IOException("first listener start")) : Task.CompletedTask);
        var block = new NativeUiProxyBlock(proxy, _ => failed.TrySetResult());
        var failedEndpoint = proxy.WaitForAddressAsync(CancellationToken.None);
        var run = block.RunAsync(new(stopping.Token, () => running.TrySetResult()));
        try
        {
            await failed.Task;
            await Assert.ThrowsAsync<IOException>(() => failedEndpoint);
            Assert.Equal(1, attempts);
            Assert.False(running.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            await Assert.ThrowsAsync<IOException>(() => proxy.WaitForAddressAsync(CancellationToken.None));
            Assert.Equal(1, attempts);
            Assert.True(block.RequestRetry());
            var address = await proxy.WaitForAddressAsync(CancellationToken.None);
            await running.Task;
            Assert.Equal(2, attempts);
            Assert.Equal(proxy.Address, address);
            Assert.False(block.RequestRetry());
            Assert.Equal(2, attempts);
        }
        finally { stopping.Cancel(); await run; }
        Assert.False(block.RequestRetry());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.StartAsync());
    }

    [Fact]
    public async Task StopWhileWaitingForManualRetryDisposesWithoutAnotherAttempt()
    {
        var attempts = 0;
        var failed = Signal();
        using var stopping = new CancellationTokenSource();
        await using var proxy = Proxy(_ =>
        {
            attempts++;
            return Task.FromException(new IOException("listener unavailable"));
        });
        var block = new NativeUiProxyBlock(proxy, _ => failed.TrySetResult());
        var run = block.RunAsync(new(stopping.Token,
            () => throw new InvalidOperationException("A failed listener must not report running.")));
        await failed.Task;
        Assert.Equal(1, attempts);
        Assert.False(run.IsCompleted);
        stopping.Cancel();
        await run;
        Assert.Equal(1, attempts);
        Assert.False(block.RequestRetry());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.StartAsync());
    }

    [Fact]
    public async Task StandaloneExplicitStartCanRetryButReadingFailureCannotStartIt()
    {
        var attempts = 0;
        await using var proxy = Proxy(_ => ++attempts == 1
            ? Task.FromException(new IOException("first attempt")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => proxy.StartAsync());
        await Assert.ThrowsAsync<IOException>(() => proxy.WaitForAddressAsync(CancellationToken.None));
        Assert.Equal(1, attempts);
        await proxy.StartAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(proxy.Address, await proxy.WaitForAddressAsync(CancellationToken.None));
    }

    private static FrontendLoopbackProxy Proxy(Func<CancellationToken, Task> beforeStart)
        => new(new Uri("http://127.0.0.1:9321/"),
            (_, _) => throw new InvalidOperationException("No forwarding expected."), beforeStart);

    private static TaskCompletionSource Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
