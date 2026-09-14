using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class BackendReconnectPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 15)]
    [InlineData(100, 15)]
    public void GetDelay_UsesBoundedBackoff(int failedAttemptCount, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            BackendReconnectPolicy.GetDelay(failedAttemptCount));
    }

    [Fact]
    public void GetDelay_RejectsNegativeAttemptCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackendReconnectPolicy.GetDelay(-1));
    }
}
