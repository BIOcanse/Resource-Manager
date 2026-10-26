using ResourceManager.NativeUi;
using System.IO.Pipes;
using System.Text;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiSingleInstanceCoordinatorTests
{
    [Fact]
    public void ListenerStopsDuringPartialRequestAndCanRestartWithoutReleasingProcessMutex()
    {
        var identity = Guid.NewGuid().ToString("N");
        var mutex = $@"Local\ResourceManager.NativeUi.Tests.{identity}";
        var pipe = $"ResourceManager.NativeUi.Tests.{identity}";
        using var coordinator = NativeUiSingleInstanceCoordinator.Create(mutex, pipe);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.RequestReceived += (_, command) => received.TrySetResult(command);
        try
        {
            var first = coordinator.RunAsync(CancellationToken.None);
            Assert.Same(first, coordinator.RunAsync(CancellationToken.None));
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out))
            {
                client.Connect(2000);
                client.Write(Encoding.UTF8.GetBytes("partial"));
                client.Flush();
                coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            Assert.True(first.IsCompletedSuccessfully);
            Assert.False(received.Task.IsCompleted);
            using var secondary = NativeUiSingleInstanceCoordinator.Create(mutex, pipe);
            Assert.False(secondary.IsPrimary);

            using var cancellation = new CancellationTokenSource();
            var second = coordinator.RunAsync(cancellation.Token);
            Assert.NotSame(first, second);
            Assert.True(secondary.SignalRequest("show"));
            Assert.Equal("show", received.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
            cancellation.Cancel();
            second.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        finally
        {
            // Keep final mutex release on the creating thread despite async pipe IO.
            coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Dispose_IsIdempotentForPrimaryOwner()
    {
        var identity = Guid.NewGuid().ToString("N");
        var coordinator = NativeUiSingleInstanceCoordinator.Create(
            $@"Local\ResourceManager.NativeUi.Tests.{identity}",
            $"ResourceManager.NativeUi.Tests.{identity}");

        Assert.True(coordinator.IsPrimary);

        coordinator.Dispose();
        coordinator.Dispose();
    }
}
