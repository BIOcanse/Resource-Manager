using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTargetIdentityTests
{
    [Fact]
    public void ProcessTargetId_BindsPidAndStartKey()
    {
        Assert.Equal(
            "process:42:9001",
            HostManagerTargetIdentity.CreateProcessTargetId(42, 9001));
        Assert.NotEqual(
            HostManagerTargetIdentity.CreateProcessTargetId(42, 9001),
            HostManagerTargetIdentity.CreateProcessTargetId(42, 9002));
    }

    [Fact]
    public void ProcessMemoryPolicyTargetId_IsASeparateStablePolicyLane()
    {
        Assert.Equal(
            "process-memory-policy:42:9001",
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(42, 9001));
        Assert.NotEqual(
            HostManagerTargetIdentity.CreateProcessTargetId(42, 9001),
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(42, 9001));
    }

    [Theory]
    [InlineData(0, 1UL)]
    [InlineData(-1, 1UL)]
    [InlineData(1, 0UL)]
    public void ProcessTargetId_RejectsIncompleteIdentity(int processId, ulong processStartKey)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HostManagerTargetIdentity.CreateProcessTargetId(processId, processStartKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                processId,
                processStartKey));
    }
}
