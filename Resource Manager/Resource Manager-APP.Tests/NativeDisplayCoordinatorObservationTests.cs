using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class NativeDisplayCoordinatorObservationTests
{
    [Fact]
    public void ToMilliNits_PreservesObservedHardwarePrecision()
    {
        Assert.Equal(0u, NativeDisplayCoordinatorObservationProjector.ToMilliNits(0, allowZero: true));
        Assert.Equal(40u, NativeDisplayCoordinatorObservationProjector.ToMilliNits(0.04, allowZero: true));
        Assert.Equal(972223u, NativeDisplayCoordinatorObservationProjector.ToMilliNits(972.223, allowZero: false));
        Assert.Null(NativeDisplayCoordinatorObservationProjector.ToMilliNits(0, allowZero: false));
    }
}
