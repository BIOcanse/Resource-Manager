using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDurableStorePathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rm-durable-store-paths"));

    [Fact]
    public void SharedDefaultsAreTheExactProductionProfileAndIncludeEveryStore()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(bytes.ToArray(), HostManagerDurableStorePaths.ReadDefaultProfileBytes());

        var stores = HostManagerDurableStorePaths.Resolve(Root).ToDictionary(store => store.Kind);
        Assert.Equal(5, stores.Count);
        AssertStore(stores["transaction-journal"], "UserData/host-manager/transaction-journal/journal.bin");
        AssertStore(stores["applied-ownership"], "UserData/host-manager/applied-ownership/ledger.bin");
        AssertStore(stores["operation-coordinator"], "UserData/HostManager/operation-coordinator/operation-coordinator-v1.bin");
        AssertStore(stores["rollback-state"], "UserData/HostManager/RollbackState/generation-00000001/rollback-state.json",
            "UserData/HostManager/RollbackState/rollback-state.root.manifest");
        AssertStore(stores["memory-cleanup-attempt-journal"],
            "UserData/HostManager/MemoryCleanup/generation-00000001/host-manager-memory-cleanup-attempts.json");
    }

    [Fact]
    public void ConfiguredNamesAndSubdirectoriesAreResolvedWithoutFilenameInference()
    {
        var profile = Profile();
        profile["host_recreate"]!["transaction_journal"]!["journal_relative_path"] = "custom/journal-data";
        profile["host_recreate"]!["applied_ownership"]!["ledger_relative_path"] = "custom/ownership.data";
        profile["host_recreate"]!["operation_coordinator"]!["canonical_envelope_relative_path"] = "custom/operation.bin";
        var stores = Resolve(profile).ToDictionary(store => store.Kind);
        AssertStore(stores["transaction-journal"], "UserData/custom/journal-data");
        AssertStore(stores["applied-ownership"], "UserData/custom/ownership.data");
        AssertStore(stores["operation-coordinator"], "UserData/HostManager/custom/operation.bin");

        var runtime = HostManagerTransactionJournalRuntime.ResolveStoragePaths(
            HostManagerAuthorityRetirementManager.CompileProductionPaths(Root).RuntimeDataRoot,
            new CompiledHostManagerTransactionJournalRecreatePlan(1, 1024, 1, 1024,
                "custom/journal-data", "custom/payloads"));
        Assert.Equal(stores["transaction-journal"].CanonicalPath, runtime.JournalPath);
    }

    [Fact]
    public void FixedGenerationPathsMatchRuntimeOwners()
    {
        var stores = HostManagerDurableStorePaths.Resolve(Root).ToDictionary(store => store.Kind);
        var contentRoot = Path.Combine(Root, "Bin", "ResourceManager");
        var rollback = JsonHostManagerRollbackStateStore.ResolveProductionPaths(contentRoot, Root);
        Assert.Equal(stores["rollback-state"].CanonicalPath, rollback.CanonicalPath);
        Assert.Equal(stores["rollback-state"].ManifestPath, rollback.RootManifestPath);
        Assert.Equal(stores["memory-cleanup-attempt-journal"].CanonicalPath,
            HostManagerMemoryCleanupAttemptJournal.ResolveProductionPaths(contentRoot, Root).CanonicalPath);
    }

    [Theory]
    [InlineData("transaction_journal", "journal_relative_path")]
    [InlineData("applied_ownership", "ledger_relative_path")]
    [InlineData("operation_coordinator", "canonical_envelope_relative_path")]
    public void MissingOrInvalidConfiguredLocationNeverUsesTheDefault(string owner, string property)
    {
        var profile = Profile();
        profile["host_recreate"]![owner]!.AsObject().Remove(property);
        Assert.Throws<InvalidDataException>(() => Resolve(profile));
        foreach (var value in new[] { "", "../escaped.bin", "custom//data", "C:escape.bin", "C:/escape.bin", "/escape.bin" })
        {
            profile["host_recreate"]![owner]![property] = value;
            Assert.Throws<InvalidDataException>(() => Resolve(profile));
        }
        profile["host_recreate"]![owner]![property] = null;
        Assert.Throws<InvalidDataException>(() => Resolve(profile));
    }

    [Fact]
    public void ProfileSeparatorNormalizationMatchesTheCompilerButCompiledPathsRemainStrict()
    {
        var profile = Profile();
        profile["host_recreate"]!["applied_ownership"]!["ledger_relative_path"] = @"custom\ownership.data";
        AssertStore(Resolve(profile).Single(store => store.Kind == "applied-ownership"), "UserData/custom/ownership.data");
        Assert.Throws<InvalidDataException>(() => HostManagerDurableStorePaths.ResolveRelativePath(
            HostManagerDurableStorePaths.ResolveRuntimeDataRoot(Root), @"custom\ownership.data"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"host_recreate\":{},\"host_recreate\":{}}")]
    [InlineData("{\"host_recreate\":{\"transaction_journal\":{\"journal_relative_path\":\"one\",\"journal_relative_path\":\"two\"}}}")]
    [InlineData("{")]
    public void MissingAmbiguousOrMalformedProfileFailsClosed(string profile)
        => Assert.Throws<InvalidDataException>(() => HostManagerDurableStorePaths.Resolve(Root, Encoding.UTF8.GetBytes(profile)));

    private static JsonObject Profile() => JsonNode.Parse(HostManagerDurableStorePaths.ReadDefaultProfileBytes())!.AsObject();
    private static IReadOnlyList<HostManagerDurableStorePath> Resolve(JsonObject profile)
        => HostManagerDurableStorePaths.Resolve(Root, Encoding.UTF8.GetBytes(profile.ToJsonString()));
    private static void AssertStore(HostManagerDurableStorePath store, string canonicalRelative, string? manifestRelative = null)
    {
        var canonical = Path.GetFullPath(Path.Combine(Root, canonicalRelative));
        Assert.Equal(canonical, store.CanonicalPath);
        Assert.Equal(manifestRelative is null ? canonical + ".root.manifest"
            : Path.GetFullPath(Path.Combine(Root, manifestRelative)), store.ManifestPath);
    }
}
