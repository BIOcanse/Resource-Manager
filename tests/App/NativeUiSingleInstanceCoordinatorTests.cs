using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiSingleInstanceCoordinatorTests
{
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
