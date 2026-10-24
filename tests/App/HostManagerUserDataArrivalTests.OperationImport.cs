using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerUserDataArrivalTests
{
    [Theory]
    [InlineData(HostManagerOperationStates.Succeeded)]
    [InlineData(HostManagerOperationStates.Canceled)]
    public async Task ImportAcceptsRealSettledOperationHistoryWithRetainedPayloadsAndReceipts(string state)
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var image = await HostManagerOperationCoordinatorOwnerTests.CreateOperationImportImageAsync(
            Path.Combine(fixture.Root, "operation-producer"), state);
        var target = InstallOperationImage(fixture.Target, image);
        Assert.False(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(target.CanonicalPath));

        fixture.Arrive(true);

        fixture.AssertUsable();
        // The incoming archive has no operation store: successful replacement
        // demonstrates retained target history did not block the real import.
        Assert.False(File.Exists(target.CanonicalPath));
        Assert.False(File.Exists(target.ManifestPath));
    }

    [Theory]
    [InlineData(HostManagerOperationStates.Running)]
    [InlineData(HostManagerOperationStates.Queued)]
    [InlineData(HostManagerOperationStates.StateUncertain)]
    public async Task ImportRejectsRealOutstandingOperationImagesAndPreservesAuthority(string state)
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var image = await HostManagerOperationCoordinatorOwnerTests.CreateOperationImportImageAsync(
            Path.Combine(fixture.Root, "operation-producer"), state);
        var target = InstallOperationImage(fixture.Target, image);
        var authorityPath = HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath;
        var authority = File.ReadAllBytes(authorityPath);
        Assert.True(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(target.CanonicalPath));

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Arrive(true));

        Assert.Contains("operation-coordinator", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(image.Canonical, File.ReadAllBytes(target.CanonicalPath));
        Assert.Equal(image.Manifest, File.ReadAllBytes(target.ManifestPath));
        Assert.Equal(authority, File.ReadAllBytes(authorityPath));
        fixture.AssertUsable();
    }

    [Fact]
    public async Task ImportRejectsARealLiveOperationStoreWhoseManifestWasLost()
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var image = await HostManagerOperationCoordinatorOwnerTests.CreateOperationImportImageAsync(
            Path.Combine(fixture.Root, "operation-producer"), HostManagerOperationStates.Running);
        var target = InstallOperationImage(fixture.Target, image);
        File.Delete(target.ManifestPath);
        var authorityPath = HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath;
        var authority = File.ReadAllBytes(authorityPath);

        var error = Assert.Throws<InvalidDataException>(() => fixture.Arrive(true));

        Assert.Contains("manifest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(image.Canonical, File.ReadAllBytes(target.CanonicalPath));
        Assert.False(File.Exists(target.ManifestPath));
        Assert.Equal(authority, File.ReadAllBytes(authorityPath));
        fixture.AssertUsable();
    }

    private static HostManagerDurableStorePath InstallOperationImage(string targetRoot,
        HostManagerOperationCoordinatorOwnerTests.OperationImportImage image)
    {
        var target = HostManagerDurableStorePaths.Resolve(targetRoot).Single(store => store.Kind == "operation-coordinator");
        Directory.CreateDirectory(Path.GetDirectoryName(target.CanonicalPath)!);
        File.WriteAllBytes(target.CanonicalPath, image.Canonical);
        File.WriteAllBytes(target.ManifestPath, image.Manifest);
        return target;
    }
}
