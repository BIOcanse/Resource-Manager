using System.Runtime.CompilerServices;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class NativeSoftwareIdentityCatalogCapacityTests(ITestOutputHelper output)
{
    [Fact]
    public void EmptySessionUsesActualStorageAndRejectsInsufficientMaximumBudget()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog;
        var configuration = NativeSoftwareIdentityCatalogWorkspace.CreateConfiguration(plan);
        using var session = new NativeSoftwareIdentityCatalogSession(in configuration);

        Assert.InRange(session.Capacity.ResidentByteCount, 1UL, configuration.ResidentByteBudget);
        Assert.Equal(configuration.MaximumEntryCount, session.Capacity.EntryCapacity);
        Assert.Equal(configuration.MaximumCatalogKeyByteCount, session.Capacity.CatalogKeyByteCapacity);
        Assert.Equal(configuration.AliasIndexCapacity, session.Capacity.AliasIndexCapacity);
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, session.Snapshot(out var summary));
        Assert.Equal(0U, summary.KeyByteCount);
        Assert.Equal(session.Capacity.ResidentByteCount, summary.ResidentByteCount);
        configuration.ResidentByteBudget = 4096;
        Assert.Throws<InvalidOperationException>(() => new NativeSoftwareIdentityCatalogSession(in configuration));
    }

    [Fact]
    public void BundledProjectionReportsActualResidentBytes()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog;
        var configuration = NativeSoftwareIdentityCatalogWorkspace.CreateConfiguration(plan);
        var path = Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Resources",
            "SoftwareIdentity", "software-identities.v1.json");
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<SoftwareIdentityCatalogDocument>(stream, JsonSerializerOptions.Web);
        var projection = NativeSoftwareIdentityCatalogProjector.ProjectBundled(
            Assert.IsType<SoftwareIdentityCatalogDocument>(document), plan);
        using var workspace = new NativeSoftwareIdentityCatalogWorkspace(plan);
        var emptyBytes = workspace.Session.Capacity.ResidentByteCount;

        projection.Load(workspace.Session);

        Assert.Equal(NativeSoftwareIdentityStatus.Ok, workspace.Session.Snapshot(out var summary));
        Assert.Equal((uint)projection.Entries.Length, summary.EntryCount);
        Assert.Equal((uint)projection.Aliases.Length, summary.AliasCount);
        Assert.Equal((uint)projection.Roots.Length, summary.RootCount);
        Assert.Equal((uint)projection.KeyBytes.Length, summary.KeyByteCount);
        Assert.Equal(summary.ResidentByteCount, workspace.Session.Capacity.ResidentByteCount);
        Assert.Equal(ExpectedResidentBytes(emptyBytes, projection, configuration), summary.ResidentByteCount);
        var capacity = plan.Recreate.Capacity;
        // Native slot sizes are asserted by the catalog's native capacity test.
        // The former Session also retained a second 176-byte Bank value.
        var previousResidentBytes = emptyBytes + 176UL + (ulong)capacity.MaximumEntryCount * 8UL
            + 2UL * ((ulong)capacity.MaximumEntryCount * 56UL
                + (ulong)capacity.MaximumAliasCount * 32UL
                + (ulong)capacity.MaximumRootCount * 24UL
                + (ulong)capacity.MaximumCatalogKeyByteCount
                + (ulong)(capacity.EntryIndexCapacity + capacity.IdentityIndexCapacity
                    + capacity.AliasIndexCapacity + capacity.RootIndexCapacity) * 4UL);
        output.WriteLine($"Catalog: entries={summary.EntryCount}, aliases={summary.AliasCount}, "
            + $"roots={summary.RootCount}, keyBytes={summary.KeyByteCount}, "
            + $"emptyResidentBytes={emptyBytes}, loadedResidentBytes={summary.ResidentByteCount}.");
        var prohibited = plan.Recreate.ProhibitedExecutableAliases.ToHashSet(StringComparer.Ordinal);
        (string Name, NativeSoftwareIdentityCatalogProjection Projection)[] runtimeProjections =
        [
            ("adapted", ProjectDefinitions("BuildAdaptedDefinitions",
                Array.Empty<AdapterSoftwareRegistration>(), Array.Empty<RuntimeSoftwareAttribution>(), prohibited, null)),
            ("controlled", ProjectDefinitions("BuildControlledDefinitions",
                Array.Empty<ControlledSoftwareRegistration>(), Array.Empty<RuntimeSoftwareAttribution>(), prohibited, null)),
            ("self", ProjectDefinitions("BuildSelfDefinitions")),
            ("known", NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnown([], plan))
        ];
        var totalResidentBytes = summary.ResidentByteCount;
        foreach (var (name, runtimeProjection) in runtimeProjections)
        {
            using var runtimeWorkspace = new NativeSoftwareIdentityCatalogWorkspace(plan);
            runtimeProjection.Load(runtimeWorkspace.Session);
            var residentBytes = runtimeWorkspace.Session.Capacity.ResidentByteCount;
            Assert.Equal(ExpectedResidentBytes(emptyBytes, runtimeProjection, configuration), residentBytes);
            totalResidentBytes += residentBytes;
            output.WriteLine($"Runtime {name}: entries={runtimeProjection.Entries.Length}, "
                + $"aliases={runtimeProjection.Aliases.Length}, roots={runtimeProjection.Roots.Length}, "
                + $"keyBytes={runtimeProjection.KeyBytes.Length}, residentBytes={residentBytes}.");
        }
        output.WriteLine($"Default allocation before={previousResidentBytes} bytes per session; "
            + $"five sessions before={previousResidentBytes * 5}, after={totalResidentBytes} bytes "
            + "(bundled + adapted + controlled + self + known; no registrations or installed records; self roots use test output paths).");

        NativeSoftwareIdentityCatalogProjection ProjectDefinitions(string name, params object?[] arguments)
        {
            var method = typeof(NativeRuntimeProcessAttributionResolver).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var definitions = Assert.IsAssignableFrom<IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition>>(
                method.Invoke(null, arguments));
            return NativeSoftwareIdentityCatalogProjector.Project(definitions, new(), plan);
        }
    }

    [Fact]
    public void ReplacementGrowsShrinksAndPreservesCatalogWhenRejected()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var recreate = root["host_recreate"]!["software_identity_catalog"]!;
            recreate["prohibited_executable_aliases"] = new System.Text.Json.Nodes.JsonArray();
            recreate["launcher_tokens"] = new System.Text.Json.Nodes.JsonArray();
            recreate["managed_child_segments"] = new System.Text.Json.Nodes.JsonArray();
        }).SoftwareIdentityCatalog;
        var configuration = NativeSoftwareIdentityCatalogWorkspace.CreateConfiguration(plan);
        using var session = new NativeSoftwareIdentityCatalogSession(in configuration);
        var emptyBytes = session.Capacity.ResidentByteCount;
        var empty = Project([]);
        var small = Project([Entry("alpha")]);
        var large = Project([Entry("alpha"), Entry("beta")]);

        Assert.Equal(NativeSoftwareIdentityStatus.Ok, Replace(session, small, 1));
        var smallBytes = session.Capacity.ResidentByteCount;
        Assert.Equal(ExpectedResidentBytes(emptyBytes, small, configuration), smallBytes);
        Assert.True(smallBytes > emptyBytes);
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, Replace(session, large, 2));
        var largeBytes = session.Capacity.ResidentByteCount;
        Assert.Equal(ExpectedResidentBytes(emptyBytes, large, configuration), largeBytes);
        Assert.True(largeBytes > smallBytes);

        configuration.Generation++;
        configuration.ResidentByteBudget = largeBytes;
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, session.Reconfigure(in configuration));
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, session.Snapshot(out var before));
        Assert.Equal(NativeSoftwareIdentityStatus.InvalidArgument, Replace(session, large, 3));
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, session.Snapshot(out var after));
        Assert.Equal(before.CatalogGeneration, after.CatalogGeneration);
        Assert.Equal(before.CatalogFingerprintLow, after.CatalogFingerprintLow);
        Assert.Equal(before.CatalogFingerprintHigh, after.CatalogFingerprintHigh);
        Assert.Equal(largeBytes, session.Capacity.ResidentByteCount);

        Assert.Equal(NativeSoftwareIdentityStatus.Ok, Replace(session, empty, 3));
        Assert.Equal(emptyBytes, session.Capacity.ResidentByteCount);
        Assert.Equal(NativeSoftwareIdentityStatus.Ok, Replace(session, small, 4));
        Assert.Equal(smallBytes, session.Capacity.ResidentByteCount);

        NativeSoftwareIdentityCatalogProjection Project(SoftwareIdentityCatalogEntry[] entries)
            => NativeSoftwareIdentityCatalogProjector.ProjectBundled(new("1.0.0", entries), plan);
    }

    private static SoftwareIdentityCatalogEntry Entry(string name)
        => new(name, name, SoftwareKinds.Other, "test", ExecutableNames: [name]);

    private static ulong ExpectedResidentBytes(ulong emptyBytes, NativeSoftwareIdentityCatalogProjection projection,
        NativeSoftwareIdentityCatalogConfiguration configuration)
        => emptyBytes
            + (ulong)projection.Entries.Length * (56UL + 8UL)
            + (ulong)projection.Aliases.Length * 32UL
            + (ulong)projection.Roots.Length * 24UL
            + (ulong)projection.KeyBytes.Length
            + IndexCapacity(projection.Entries.Length, configuration.MaximumEntryCount, configuration.EntryIndexCapacity) * 4UL
            + IndexCapacity(projection.Entries.Length, configuration.MaximumEntryCount, configuration.IdentityIndexCapacity) * 4UL
            + IndexCapacity(projection.Aliases.Length, configuration.MaximumAliasCount, configuration.AliasIndexCapacity) * 4UL
            + IndexCapacity(projection.Roots.Length, configuration.MaximumRootCount, configuration.RootIndexCapacity) * 4UL;

    private static uint IndexCapacity(int count, uint maximum, uint configured)
        => count == 0 ? 0 : Math.Min(configured,
            BitOperations.RoundUpToPowerOf2((uint)count) * (configured / BitOperations.RoundUpToPowerOf2(maximum)));

    private static NativeSoftwareIdentityStatus Replace(
        NativeSoftwareIdentityCatalogSession session,
        NativeSoftwareIdentityCatalogProjection projection,
        ulong epoch)
    {
        var input = new NativeSoftwareIdentityCatalogReplaceInput
        {
            AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogReplaceInput>(),
            ConfigurationGeneration = session.ConfigurationGeneration,
            CatalogGeneration = epoch,
            OperationEpoch = epoch,
            EntryCount = (uint)projection.Entries.Length,
            AliasCount = (uint)projection.Aliases.Length,
            RootCount = (uint)projection.Roots.Length,
            KeyByteCount = (uint)projection.KeyBytes.Length,
            ProhibitedAliasCount = projection.ProhibitedAliasCount,
            LauncherTokenCount = projection.LauncherTokenCount,
            ManagedChildRuleCount = projection.ManagedChildRuleCount,
            ValidMask = (ulong)NativeSoftwareIdentityCatalogReplaceValidity.Required
        };
        return session.Replace(in input, projection.Entries, projection.Aliases, projection.Roots, projection.KeyBytes);
    }
}
