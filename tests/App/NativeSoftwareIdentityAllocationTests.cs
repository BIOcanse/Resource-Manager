using System.Text;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class NativeSoftwareIdentityAllocationTests
{
    [Theory]
    [InlineData(@"D:\Games\One\..\Two\Game.exe", "d:/games/two/game.exe")]
    [InlineData(@"D:\", "d:/")]
    [InlineData(@"\\Server\Share\Folder\", "//server/share/folder")]
    [InlineData(@"\\Server\Share\", "//server/share")]
    [InlineData(@"D:\Games\Trailing\", "d:/games/trailing")]
    [InlineData("\"D:\\Games\\Quoted\\\"", "d:/games/quoted")]
    [InlineData("D:\\Games\\𐐀Demo\\", "d:/games/𐐨demo")]
    [InlineData("D:\\Games\\Σigma\\", "d:/games/σigma")]
    public void CanonicalPathPreservesNormalizedValues(string input, string expected)
    {
        var canonical = NativeSoftwareIdentityCatalogProjector.CanonicalPath(input);

        Assert.Equal(LegacyCanonicalPath(input), canonical);
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void CanonicalPathPreservesEnvironmentExpansion()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEMP")));
        const string input = "%TEMP%\\Identity Catalog\\";

        Assert.Equal(
            LegacyCanonicalPath(input),
            NativeSoftwareIdentityCatalogProjector.CanonicalPath(input));
    }

    [Fact]
    public void CanonicalPath_WarmedStableInputAllocatesOnlyItsPathStrings()
    {
        const string input = @"D:\Games\Example\game.exe";
        const string expected = "d:/games/example/game.exe";
        const int iterations = 1_024;

        for (var index = 0; index < 64; index++)
        {
            _ = NativeSoftwareIdentityCatalogProjector.CanonicalPath(input);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var canonical = NativeSoftwareIdentityCatalogProjector.CanonicalPath(input);
            if (!string.Equals(canonical, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Canonical path changed during the warm allocation check.");
            }
            GC.KeepAlive(canonical);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.InRange(allocated, 0, iterations * (4L * expected.Length + 96));
    }

    [Fact]
    public void RuntimeKnownProjectionPreservesCanonicalRootsWhenSourceChanges()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog;
        var first = NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnown(
            [CreateSoftware("windows-installed:client", @"D:\Games\One\..\Two")],
            plan);
        var changed = NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnown(
            [CreateSoftware("windows-installed:client", @"D:\Games\Three")],
            plan);

        Assert.Equal("d:/games/two", Assert.Single(
            Assert.IsType<RuntimeSoftwareAttribution>(
                first.Payloads.RequireEntry(first.Entries[0].EntryHandle).RuntimeAttribution).RootPaths));
        var firstRoot = Assert.Single(first.Roots);
        Assert.Equal(
            "d:/games/two",
            Encoding.UTF8.GetString(first.KeyBytes, checked((int)firstRoot.KeyOffset), checked((int)firstRoot.KeyLength)));
        Assert.Equal("d:/games/three", Assert.Single(
            Assert.IsType<RuntimeSoftwareAttribution>(
                changed.Payloads.RequireEntry(changed.Entries[0].EntryHandle).RuntimeAttribution).RootPaths));
        var changedRoot = Assert.Single(changed.Roots);
        Assert.Equal(
            "d:/games/three",
            Encoding.UTF8.GetString(changed.KeyBytes, checked((int)changedRoot.KeyOffset), checked((int)changedRoot.KeyLength)));
    }

    [Fact]
    public void RuntimeKnownProjectionReusesResolverOwnedCanonicalAttribution()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog;
        var roots = new[] { "d:/games/two" };
        var attribution = new RuntimeSoftwareAttribution(
            "windows-installed:client",
            "Game Client",
            SoftwareKinds.Other,
            "other",
            roots);
        var projection = NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnownFromCanonicalAttributions(
            [attribution],
            plan);
        var projectedAttribution = Assert.IsType<RuntimeSoftwareAttribution>(
            projection.Payloads.RequireEntry(projection.Entries[0].EntryHandle).RuntimeAttribution);

        Assert.Same(attribution, projectedAttribution);
        Assert.Same(roots, projectedAttribution.RootPaths);
        var root = Assert.Single(projection.Roots);
        Assert.Equal(
            "d:/games/two",
            Encoding.UTF8.GetString(projection.KeyBytes, checked((int)root.KeyOffset), checked((int)root.KeyLength)));
    }

    [Fact]
    public void MatchKnown_WarmQueryUsesStackScratchAndPreservesMatchValues()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog;
        var software = CreateSoftware("windows-installed:client-two", @"D:\Games\Two");
        var projection = NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnown([software], plan);
        using var catalog = new NativeRuntimeSourceCatalog(projection, plan);
        var matched = catalog.ObserveKnown(new RuntimeProcessIdentity(
            7001,
            null,
            "client",
            @"D:\Games\Two\Client.exe",
            false,
            FileDescription: "Game Client",
            ProductName: "Game Client",
            WindowTitle: "Running Client"));

        Assert.False(matched.Conflict);
        var candidate = Assert.Single(matched.Candidates);
        Assert.Equal(software.Id, candidate.Attribution.Id);
        Assert.Equal("d:/games/two", Assert.Single(candidate.Attribution.RootPaths));

        var longWindowTitle = $"Other Window {new string('x', 500)} 测试";
        var unmatchedProcess = new RuntimeProcessIdentity(
            7002,
            null,
            "unrelated",
            @"D:\Elsewhere\Unrelated.exe",
            false,
            FileDescription: "Another Tool",
            ProductName: "Another Product",
            ApplicationUserModelId: "Vendor.OtherApp",
            WindowApplicationUserModelId: "Vendor.OtherApp.Window",
            WindowTitle: longWindowTitle);
        const int iterations = 1_024;
        for (var index = 0; index < 64; index++)
        {
            AssertNoMatch(catalog.ObserveKnown(unmatchedProcess));
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var observation = catalog.ObserveKnown(unmatchedProcess);
            if (observation.Conflict || observation.Candidates.Count != 0)
            {
                throw new InvalidOperationException("The warm known-query result changed during allocation measurement.");
            }
            GC.KeepAlive(observation);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.InRange(allocated, 0, iterations * (4L * unmatchedProcess.ExecutablePath!.Length + 112));
    }

    private static SoftwareRecord CreateSoftware(string id, string rootPath)
        => new(
            id,
            "Game Client",
            SoftwareKinds.Other,
            "other",
            "installed",
            ["test"],
            [rootPath],
            string.Empty,
            new SoftwareOperationCapabilities(false, "None", "Unavailable", string.Empty),
            null);

    private static string? LegacyCanonicalPath(string value)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
            .Replace('\\', '/')
            .ToLowerInvariant();
        var root = Path.GetPathRoot(path.Replace('/', Path.DirectorySeparatorChar));
        var canonicalRoot = root?.Replace('\\', '/').ToLowerInvariant();
        return canonicalRoot is not null && path.Equals(canonicalRoot, StringComparison.Ordinal)
            ? path
            : path.TrimEnd('/');
    }

    private static void AssertNoMatch(NativeSourceCatalogObservation observation)
    {
        Assert.False(observation.Conflict);
        Assert.Empty(observation.Candidates);
    }
}
