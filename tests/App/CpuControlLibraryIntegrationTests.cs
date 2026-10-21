using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class CpuControlLibraryIntegrationTests
{
    [Fact]
    public void PublicOriginalOperationsAreIndependentAndUnselectedReadsDoNotOpenHardware()
    {
        Assert.All(new[] { typeof(IntelMsrReader), typeof(IntelMsrCatalogIdentity), typeof(IntelMsrTopologyProjection),
            typeof(IntelRaplEncoding), typeof(IntelOcMailbox), typeof(PawnIoIntelCpuHardware), typeof(IIntelCpuHardware),
            typeof(AdlxTuningHardware), typeof(IAdlxTuningHardware), typeof(AdlxTuningValue), typeof(NativeMetricSnapshotCounterPair) },
            type => Assert.Same(typeof(ReadWriteCore).Assembly, type.Assembly));
        var opens = 0;
        var reader = new IntelMsrReader(() => { opens++; throw new IOException("No driver requested"); });
        var core = new IntelMsrCatalogIdentity(0, "windows-cpu-core|0,1", new(0, 0), 0x906E0, null,
            IntelMsrTargetKind.PhysicalCore, true, true);
        Assert.Empty(reader.Read([core], new Dictionary<string, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(reader.Read([core], new Dictionary<string, IReadOnlySet<MetricFieldCode>> {
            [core.ExactIdentityKey] = new HashSet<MetricFieldCode> { MetricFieldCode.IntelPackageDtsTemperature } }));
        Assert.Equal(0, opens);
        _ = new AdlxTuningHardware();
        var missing = Path.Combine(Path.GetTempPath(), "missing-intel-module-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<FileNotFoundException>(() => new PawnIoIntelCpuHardware(missing, missing + ".dll"));
        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public void PublicTopologyProjectionKeepsPhysicalDomainsAndUnavailableVersusSuccessfulEmpty()
    {
        var failed = IntelMsrTopologyProjection.Create(null);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.CoreStatus);
        Assert.Empty(failed.Identities);
        var data = new CpuThreadCpuidData(new("GenuineIntel", 6, 0x9E, 0, null, [], []), 0x906E0,
            null, 9, null, null, null, []) { IntelDtsSupported = true, IntelPackageThermalSupported = true,
                IntelPerformanceFeedbackSupported = true };
        var topology = new CpuOsTopologyInformation([new("core", 0, true, [0, 1])], []) {
            Packages = [new("package", [0, 1])], Dies = [], ThreadCpuid = [new(0, data), new(1, data)] };
        var captured = IntelMsrTopologyProjection.Create(topology);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, captured.CoreStatus);
        Assert.Equal(4, captured.Identities.Count);
        var core = Assert.Single(captured.Identities, item => item.Kind == IntelMsrTargetKind.PhysicalCore);
        var package = Assert.Single(captured.Identities, item => item.Kind == IntelMsrTargetKind.Package);
        Assert.True(core.AppliesTo(MetricFieldCode.IntelCoreDtsTemperature));
        Assert.False(core.AppliesTo(MetricFieldCode.IntelPackageDtsTemperature));
        Assert.True(package.AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        Assert.False(package.AppliesTo(MetricFieldCode.IntelAperfCounter));
        var amd = data with { Identity = new("AuthenticAMD", 0x19, 0x61, 0, null, [], []) };
        var empty = IntelMsrTopologyProjection.Create(topology with { ThreadCpuid = [new(0, amd), new(1, amd)] });
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.CoreStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.PackageStatus);
        Assert.Empty(empty.Identities);
    }
}
