using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using Xunit;

namespace ResourceManager.App.Tests;

public sealed class ProcessMemoryPriorityRecoveryReaderTests
{
    [Fact]
    public void LimitedInformationReadReturnsExactIdentityAndCurrentMemoryPriority()
    {
        var writer = CreateWriter();
        var identity = writer.ReadProcessInstanceForRecovery(Environment.ProcessId);
        var memory = writer.TryReadMemoryPriority(Environment.ProcessId);

        var result = writer.ReadMemoryPriorityStateForRecovery(Environment.ProcessId);

        Assert.Equal(RecoveryReadStatus.Found, identity.Status);
        Assert.NotNull(memory);
        Assert.Equal(RecoveryReadStatus.Found, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(Environment.ProcessId, result.Value.ProcessId);
        Assert.Equal(identity.Value!.StartedAt.ToFileTime(), result.Value.StartedAt.ToFileTime());
        Assert.Equal(memory.MemoryPriority, result.Value.MemoryPriority);
        Assert.InRange(result.Value.MemoryPriority, 1U, 5U);
    }

    [Fact]
    public void MissingProcessIsExplicitlyClassified()
    {
        var result = CreateWriter().ReadMemoryPriorityStateForRecovery(int.MaxValue);

        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidProcessIdsAreRejected(int processId)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateWriter().ReadMemoryPriorityStateForRecovery(processId));

    private static WindowsProcessResourcePolicyWriter CreateWriter()
        => new(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256));
}
