using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsThreadPlacementIdentityTests
{
    [Fact]
    public void BoundedPlacementInventoryReadsOnlyThisTestProcessesThreadFacts()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var start = checked((ulong)process.StartTime.ToFileTime());
        var writer = new WindowsProcessResourcePolicyWriter(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256));
        var read = writer.ReadProcessThreadIdealProcessors(process.Id, start, 262144);
        Assert.Equal(RecoveryReadStatus.Found, read.Status);
        Assert.NotEmpty(read.Value!);
        Assert.All(read.Value!, thread =>
        {
            Assert.Equal(process.Id, thread.ProcessId);
            Assert.Equal(start, thread.ProcessStartKey);
            Assert.True(thread.ThreadStartKey >= start);
        });
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)] [InlineData(10_000L)] [InlineData(5_000_000L)] [InlineData(10_000_000L)]
    public async Task CurrentThreadCpuSetsRejectEvenSmallCreationIdentityMismatch(long differenceTicks)
    {
        var completion = new TaskCompletionSource<ProcessResourcePolicyWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var writer = new WindowsProcessResourcePolicyWriter(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256));
                var processId = Environment.ProcessId;
                var threadId = checked((int)GetCurrentThreadId());
                var before = writer.ReadThreadPlacementStateForRecovery(processId, threadId);
                Assert.Equal(RecoveryReadStatus.Found, before.Status);
                Assert.NotNull(before.Value);
                Assert.Empty(before.Value.CpuSetIds); // This newly owned thread has no explicit placement.
                var result = writer.TrySetThreadSelectedCpuSets(processId, threadId,
                    before.Value.CreatedAt.AddTicks(differenceTicks), before.Value.CpuSetIds);
                var after = writer.ReadThreadPlacementStateForRecovery(processId, threadId);
                Assert.Equal(RecoveryReadStatus.Found, after.Status);
                Assert.Equal(before.Value.CreatedAt, after.Value!.CreatedAt);
                Assert.Equal(before.Value.CpuSetIds, after.Value.CpuSetIds);
                completion.SetResult(result);
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Owned placement identity test" };
        thread.Start();
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(differenceTicks == 0, result.Succeeded);
            if (differenceTicks != 0) Assert.Equal("线程身份已经变化，拒绝写入 CPU Sets。", result.Message);
        }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
