using System.Text;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

public sealed class NativePortableSoftwareRegistryProjectionTests
{
    private static readonly DateTimeOffset CommandUtc = DateTimeOffset.FromUnixTimeMilliseconds(10_000);

    [Fact]
    public void ObserveProjectsCanonicalIdentityPathsTimesAndExactByteOffsets()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 100);
        var projection = CreateProjection(catalog);
        var observation = new PortableSoftwareObservation(
            " CATALOG:Tool ",
            " TOOL ",
            " Tool UI ",
            " Portable ",
            "\"C:\\Portable\\Tool\\TOOL.EXE\"",
            "c:/PORTABLE",
            IdentityConfirmed: true,
            RootPathConfirmed: true);

        var envelope = projection.ProjectObserve(
            observation,
            configurationGeneration: 7,
            operationEpoch: 9,
            CommandUtc,
            CommandUtc.AddMilliseconds(2_000));

        var input = envelope.Input;
        var expectedExecutable = "c:/portable/tool/tool.exe";
        var expectedRoot = "c:/portable";
        var expectedBytes = Encoding.UTF8.GetBytes(expectedExecutable + expectedRoot);
        Assert.Equal(expectedBytes, envelope.KeyBytes.ToArray());
        Assert.Equal(NativePortableSoftwareRegistryAbi.Version, input.AbiVersion);
        Assert.Equal(144U, input.StructSize);
        Assert.Equal(7UL, input.ConfigurationGeneration);
        Assert.Equal(9UL, input.OperationEpoch);
        Assert.Equal(10_000, input.CommandUtcMilliseconds);
        Assert.Equal(12_000, input.ObservedAtUtcMilliseconds);
        Assert.Equal(0U, input.ExecutablePathOffset);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(expectedExecutable), input.ExecutablePathLength);
        Assert.Equal(input.ExecutablePathLength, input.RootPathOffset);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(expectedRoot), input.RootPathLength);
        Assert.Equal((uint)NativePortableSoftwarePathFlags.Known, input.PathFlags);
        Assert.Equal((ulong)NativePortableSoftwareObserveValidity.Required, input.ValidMask);
        Assert.Equal("catalog:tool", catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.SoftwareId,
            input.SoftwareHandle).Text);
        Assert.Equal("tool", catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.CatalogEntryId,
            input.CatalogEntryHandle).Text);
        Assert.Equal("Tool UI", catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.DisplayName,
            input.DisplayNameHandle).Text);
        Assert.Equal("portable", catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.SoftwareKind,
            input.SoftwareKindHandle).Text);
        Assert.Equal(expectedExecutable, catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.ExecutablePath,
            input.PathHandle).Text);
        Assert.Equal(expectedRoot, catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.RootPath,
            input.RootHandle).Text);
    }

    [Fact]
    public void InvalidObservationDoesNotAllocateAnyPayloadHandle()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var projection = CreateProjection(catalog);
        var relativePath = Observation(executablePath: "portable/tool.exe");

        Assert.Throws<ArgumentException>(() => projection.ProjectObserve(
            relativePath,
            configurationGeneration: 1,
            operationEpoch: 1,
            CommandUtc,
            CommandUtc));
        Assert.Equal(0, catalog.Count);

        Assert.Throws<ArgumentException>(() => projection.ProjectObserve(
            Observation(softwareId: "catalog:one", catalogEntryId: "two"),
            configurationGeneration: 1,
            operationEpoch: 1,
            CommandUtc,
            CommandUtc));
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void FutureSkewAndUtf8LimitsAreCheckedBeforeAllocatingHandles()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var projection = new NativePortableSoftwareRegistryProjection(
            catalog,
            maximumExecutablePathByteCount: 20,
            maximumRootPathByteCount: 20,
            maximumFutureSkewMilliseconds: 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ProjectObserve(
            Observation(),
            configurationGeneration: 1,
            operationEpoch: 1,
            CommandUtc,
            CommandUtc.AddMilliseconds(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ProjectObserve(
            Observation(executablePath: "c:/portable/very-long-tool.exe"),
            configurationGeneration: 1,
            operationEpoch: 1,
            CommandUtc,
            CommandUtc));
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void PayloadExhaustionCannotLeaveHalfOfAnObservationPublished()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: ulong.MaxValue - 4);
        var projection = CreateProjection(catalog);

        Assert.Throws<InvalidOperationException>(() => projection.ProjectObserve(
            Observation(),
            configurationGeneration: 1,
            operationEpoch: 1,
            CommandUtc,
            CommandUtc));

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.IsExhausted);
    }

    [Fact]
    public void ConfirmRootAndMarkMissingUseTheSameCanonicalPayloadNamespaces()
    {
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 50);
        var projection = CreateProjection(catalog);

        var confirmation = projection.ProjectConfirmRoot(
            "CATALOG:Tool",
            "C:\\Portable\\Tool\\",
            configurationGeneration: 3,
            operationEpoch: 4,
            CommandUtc);
        var missing = projection.ProjectMarkMissing(
            "catalog:tool",
            "C:\\Portable\\Tool\\TOOL.EXE",
            configurationGeneration: 3,
            operationEpoch: 5,
            CommandUtc);

        Assert.Equal("c:/portable/tool", Encoding.UTF8.GetString(confirmation.KeyBytes.Span));
        Assert.Equal(0U, confirmation.Input.RootPathOffset);
        Assert.Equal((uint)confirmation.KeyBytes.Length, confirmation.Input.RootPathLength);
        Assert.Equal((ulong)NativePortableSoftwareConfirmRootValidity.Required, confirmation.Input.ValidMask);
        Assert.Equal(confirmation.Input.SoftwareHandle, missing.SoftwareHandle);
        Assert.Equal((ulong)NativePortableSoftwareMarkMissingValidity.Required, missing.ValidMask);
        Assert.Equal("c:/portable/tool/tool.exe", catalog.ResolveRequired(
            NativePortableSoftwarePayloadKind.ExecutablePath,
            missing.PathHandle).Text);
    }

    [Fact]
    public void CanonicalPathSupportsDriveRootsAndUncAndRejectsImplicitOrDevicePaths()
    {
        Assert.Equal(
            "c:/",
            NativePortableSoftwareRegistryProjection.CanonicalPath("C:\\", "path"));
        Assert.Equal(
            "//server/share/folder/tool.exe",
            NativePortableSoftwareRegistryProjection.CanonicalPath(
                "\\\\Server\\Share\\Folder\\Tool.exe",
                "path"));
        Assert.Throws<ArgumentException>(() =>
            NativePortableSoftwareRegistryProjection.CanonicalPath("relative\\tool.exe", "path"));
        Assert.Throws<ArgumentException>(() =>
            NativePortableSoftwareRegistryProjection.CanonicalPath("\\\\?\\C:\\tool.exe", "path"));
        Assert.Throws<ArgumentException>(() =>
            NativePortableSoftwareRegistryProjection.CanonicalPath("\"C:\\tool.exe", "path"));
    }

    [Fact]
    public void ZeroGenerationEpochAndPreUnixTimeFailClosed()
    {
        var projection = CreateProjection(new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ProjectMarkMissing(
            "catalog:tool",
            "c:/portable/tool.exe",
            configurationGeneration: 0,
            operationEpoch: 1,
            CommandUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ProjectMarkMissing(
            "catalog:tool",
            "c:/portable/tool.exe",
            configurationGeneration: 1,
            operationEpoch: 0,
            CommandUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ProjectMarkMissing(
            "catalog:tool",
            "c:/portable/tool.exe",
            configurationGeneration: 1,
            operationEpoch: 1,
            DateTimeOffset.UnixEpoch.AddMilliseconds(-1)));
    }

    private static NativePortableSoftwareRegistryProjection CreateProjection(
        NativePortableSoftwareRegistryPayloadCatalog catalog)
        => new(
            catalog,
            maximumExecutablePathByteCount: 256,
            maximumRootPathByteCount: 256,
            maximumFutureSkewMilliseconds: 2_000);

    private static PortableSoftwareObservation Observation(
        string softwareId = "catalog:tool",
        string catalogEntryId = "tool",
        string executablePath = "c:/portable/tool.exe")
        => new(
            softwareId,
            catalogEntryId,
            "Tool",
            "Portable",
            executablePath,
            "c:/portable",
            IdentityConfirmed: false,
            RootPathConfirmed: false);
}
