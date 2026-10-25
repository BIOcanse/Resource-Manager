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
        var catalog = new WindowsControlObjectCatalog(null!, [], new Access(level), null!, null!, new EmptyObservations());
        var capability = new ControlCapability("gpu.core-voltage-offset", ControlValueKinds.Number,
            false, "control-writer-not-implemented", ControlUnavailableKinds.NotImplemented, RequiredAccessLevel: ControlAccessLevels.Root);
        var target = new ControlObject("gpu:test", ControlObjectKinds.Gpu, "GPU",
            new(ControlOperatingSystems.Windows, ControlVendors.Nvidia), [capability]);
        var result = catalog.ResolveCapability(target, capability);
        Assert.Equal(ControlUnavailableKinds.NotImplemented, result.UnavailableKind);
        Assert.Equal("control-writer-not-implemented", result.UnavailableReason?.Code);
        Assert.False(result.Supported);
    }

    private sealed class Access(string current) : IControlAccessLevel
    {
        public string Current => current;
        public Task SetAsync(string level, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyObservations : ResourceManager.App.Application.Metrics.IMetricSnapshotObservationSource
    {
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
            => throw new NotSupportedException();
    }
}
