using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlCatalogLockTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("root")]
    public void MissingWriterIsNeverAdvertisedAsAnAccessLevelFix(string level)
    {
        var catalog = new WindowsControlObjectCatalog(null!, [], new Access(level), null!, null!);
        var capability = new ControlCapability("gpu.core-voltage-offset", "Voltage", ControlValueKinds.Number,
            false, "Not implemented", ControlUnavailableKinds.NotImplemented, RequiredAccessLevel: ControlAccessLevels.Root);
        var target = new ControlObject("gpu:test", ControlObjectKinds.Gpu, "GPU",
            new(ControlOperatingSystems.Windows, ControlVendors.Nvidia), [capability]);
        var result = catalog.ResolveCapability(target, capability);
        Assert.Equal(ControlUnavailableKinds.NotImplemented, result.UnavailableKind);
        Assert.Equal("Not implemented", result.UnavailableReason);
        Assert.False(result.Supported);
    }

    private sealed class Access(string current) : IControlAccessLevel
    {
        public string Current => current;
        public Task SetAsync(string level, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
