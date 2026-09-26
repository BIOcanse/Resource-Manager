using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerAppliedOwnershipConfigurationTests
{
    [Fact]
    public void DefaultProfile_ProjectsCompleteAppliedOwnershipAuthority()
    {
        var plan = Compile(ParseDefault());

        Assert.Equal(0x0002_0000U, plan.BuildSpecialize.AppliedOwnershipAbiVersion);
        Assert.Equal(65_536, plan.BuildSpecialize.CapacityLimits.AppliedOwnershipRecordCapacity);
        Assert.Equal(131_072, plan.BuildSpecialize.CapacityLimits.AppliedOwnershipPrimaryIndexCapacity);
        Assert.Equal(131_072, plan.BuildSpecialize.CapacityLimits.AppliedOwnershipPayloadIndexCapacity);
        Assert.Equal(134_217_728L, plan.BuildSpecialize.CapacityLimits.AppliedOwnershipResidentByteBudget);
        Assert.Equal(33_554_432L, plan.BuildSpecialize.CapacityLimits.AppliedOwnershipImageByteBudget);
        Assert.Single(plan.BuildSpecialize.NativeModules, static module => module == "applied_ownership");
        Assert.Contains(
            "applied_ownership",
            plan.BuildSpecialize.NativeBinaries
                .Single(static binary => binary.FileName == "ResourceManager.NativeCore.dll")
                .Modules);

        var recreate = plan.HostRecreate.AppliedOwnership;
        Assert.True(recreate.IsPublished);
        Assert.Equal(8192, recreate.RecordCapacity);
        Assert.Equal(16_384, recreate.PrimaryIndexCapacity);
        Assert.Equal(16_384, recreate.PayloadIndexCapacity);
        Assert.Equal(16_777_216L, recreate.ResidentByteBudget);
        Assert.Equal(4_194_304L, recreate.ImageByteBudget);
        Assert.Equal("host-manager/applied-ownership/ledger.bin", recreate.LedgerRelativePath);

        var hot = plan.HotPublish.AppliedOwnership;
        Assert.True(hot.IsPublished);
        Assert.Equal(59UL << 32, hot.ConfigurationGeneration);
        Assert.Equal(5000, hot.MaximumFutureSkewMilliseconds);
        Assert.Equal(5000, hot.PersistenceRetryDelayMilliseconds);
        Assert.Equal(300_000, hot.RecoveryDeadlineMilliseconds);
        Assert.Equal(30_000, hot.ShutdownDrainTimeoutMilliseconds);
        Assert.True(plan.DeploymentDigests.AppliedOwnership.IsPublished);
        Assert.Equal((byte)11, (byte)HostManagerModuleKind.AppliedOwnership);
    }

    [Fact]
    public void LoaderAndCompiler_RejectIncompleteOrLegacyAppliedOwnershipShape()
    {
        AssertLoadRejected(static profile =>
            profile["build_specialize"]!.AsObject().Remove("applied_ownership_abi_version"));
        AssertLoadRejected(static profile =>
            profile["build_specialize"]!["capacity_limits"]!
                .AsObject()
                .Remove("applied_ownership_image_byte_budget"));
        AssertLoadRejected(static profile =>
            profile["host_recreate"]!.AsObject().Remove("applied_ownership"));
        AssertLoadRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!
                .AsObject()
                .Remove("ledger_relative_path"));
        AssertLoadRejected(static profile =>
            profile["hot_publish"]!.AsObject().Remove("applied_ownership"));

        AssertRejected(static profile => profile["schema_version"] = 10);
        AssertRejected(static profile =>
            profile["build_specialize"]!["applied_ownership_abi_version"] = 0x0001_0000U);
        AssertRejected(static profile =>
            profile["build_specialize"]!["native_modules"] = new JsonArray(
                profile["build_specialize"]!["native_modules"]!
                    .AsArray()
                    .Where(static item => item?.GetValue<string>() != "applied_ownership")
                    .Select(static item => item?.DeepClone())
                    .ToArray()));
    }

    [Fact]
    public void Compiler_RejectsInvalidAppliedOwnershipCapacityAndBudget()
    {
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["record_capacity"] = 65_537);
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["primary_index_capacity"] = 4096);
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["primary_index_capacity"] = 12_288);
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["payload_index_capacity"] = 262_144);
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["resident_byte_budget"] = 134_217_729L);
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["image_byte_budget"] = 2_621_567L);
        AssertRejected(static profile =>
            profile["build_specialize"]!["capacity_limits"]!["applied_ownership_primary_index_capacity"] = 65_535);
        AssertRejected(static profile =>
            profile["build_specialize"]!["capacity_limits"]!["applied_ownership_image_byte_budget"] = 20_971_647L);
    }

    [Fact]
    public void Compiler_RejectsInvalidOrConflictingAppliedOwnershipPath()
    {
        string[] invalidPaths =
        [
            string.Empty,
            "C:/state/ledger.bin",
            "C:state/ledger.bin",
            "/state/ledger.bin",
            "\\\\server\\share\\ledger.bin",
            "state//ledger.bin",
            "state/./ledger.bin",
            "state/../ledger.bin",
            "state/"
        ];
        foreach (var path in invalidPaths)
        {
            AssertRejected(profile =>
                profile["host_recreate"]!["applied_ownership"]!["ledger_relative_path"] = path);
        }

        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["ledger_relative_path"] =
                "host-manager/transaction-journal/payloads/ownership.bin");
        AssertRejected(static profile =>
            profile["host_recreate"]!["applied_ownership"]!["ledger_relative_path"] =
                "host-manager/transaction-journal/journal.bin");
    }

    [Fact]
    public void Compiler_UsesRevisionGenerationAndIndependentModuleDigests()
    {
        var first = Compile(ParseDefault());

        var changedHotProfile = ParseDefault();
        changedHotProfile["hot_publish"]!["applied_ownership"]!["persistence_retry_delay_ms"] = 6000;
        var changedHot = Compile(changedHotProfile);
        Assert.Equal(
            first.HotPublish.AppliedOwnership.ConfigurationGeneration,
            changedHot.HotPublish.AppliedOwnership.ConfigurationGeneration);
        Assert.Equal(
            first.DeploymentDigests.AppliedOwnership.RecreateSha256,
            changedHot.DeploymentDigests.AppliedOwnership.RecreateSha256);
        Assert.NotEqual(
            first.DeploymentDigests.AppliedOwnership.HotPublishSha256,
            changedHot.DeploymentDigests.AppliedOwnership.HotPublishSha256);
        Assert.Equal(
            first.DeploymentDigests.TransactionJournal.HotPublishSha256,
            changedHot.DeploymentDigests.TransactionJournal.HotPublishSha256);

        var changedRecreateProfile = ParseDefault();
        changedRecreateProfile["host_recreate"]!["applied_ownership"]!["record_capacity"] = 4096;
        var changedRecreate = Compile(changedRecreateProfile);
        Assert.NotEqual(
            first.DeploymentDigests.AppliedOwnership.RecreateSha256,
            changedRecreate.DeploymentDigests.AppliedOwnership.RecreateSha256);
        Assert.Equal(
            first.DeploymentDigests.AppliedOwnership.HotPublishSha256,
            changedRecreate.DeploymentDigests.AppliedOwnership.HotPublishSha256);

        changedHotProfile["profile_revision"] = first.ProfileRevision + 1;
        var nextRevision = Compile(changedHotProfile);
        Assert.Equal((ulong)(first.ProfileRevision + 1) << 32, nextRevision.HotPublish.AppliedOwnership.ConfigurationGeneration);
        Assert.Equal(0U, unchecked((uint)nextRevision.HotPublish.AppliedOwnership.ConfigurationGeneration));
    }

    [Fact]
    public void DeploymentState_TracksAppliedOwnershipAndRejectsGenerationDrift()
    {
        var first = Compile(ParseDefault());
        var state = new HostManagerDeploymentState();
        state.PublishRuntimePlan(CreateRuntimePlan(1, first));

        var pending = state.Snapshot.AppliedOwnership;
        Assert.Equal(HostManagerModuleKind.AppliedOwnership, pending.Module);
        Assert.Equal(HostManagerPendingLifecycle.InitialCreate, pending.PendingLifecycle);
        Assert.Equal(
            first.DeploymentDigests.AppliedOwnership.RecreateSha256,
            pending.DesiredRecreateSha256);

        var token = state.BeginAttempt(
            HostManagerModuleKind.AppliedOwnership,
            first,
            HostManagerDeploymentOperation.InitialCreate);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            state.CompleteAttemptSucceeded(token));
        Assert.Equal(HostManagerDeploymentStatus.InSync, state.Snapshot.AppliedOwnership.Status);
        Assert.Equal(HostManagerDeploymentStatus.Initializing, state.Snapshot.TransactionJournal.Status);

        var sameGenerationDrift = ParseDefault();
        sameGenerationDrift["hot_publish"]!["applied_ownership"]!["persistence_retry_delay_ms"] = 6000;
        Assert.Throws<InvalidOperationException>(() =>
            state.PublishRuntimePlan(CreateRuntimePlan(2, Compile(sameGenerationDrift))));

        var rollback = ParseDefault();
        rollback["profile_revision"] = 19;
        Assert.Throws<InvalidOperationException>(() =>
            state.PublishRuntimePlan(CreateRuntimePlan(2, Compile(rollback))));

        var next = ParseDefault();
        next["profile_revision"] = first.ProfileRevision + 1;
        next["hot_publish"]!["applied_ownership"]!["persistence_retry_delay_ms"] = 6000;
        state.PublishRuntimePlan(CreateRuntimePlan(2, Compile(next)));
        Assert.Equal(HostManagerDeploymentStatus.Pending, state.Snapshot.AppliedOwnership.Status);
    }

    private static CompiledRuntimePlan CreateRuntimePlan(long version, CompiledHostManagerPlan hostManager)
        => CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "applied-ownership-test",
            HostManager = hostManager
        };

    private static void AssertLoadRejected(Action<JsonObject> mutate)
    {
        var profile = ParseDefault();
        mutate(profile);
        Assert.Throws<InvalidDataException>(() => Load(profile));
    }

    private static void AssertRejected(Action<JsonObject> mutate)
    {
        var profile = ParseDefault();
        mutate(profile);
        Assert.Throws<InvalidDataException>(() => Compile(profile));
    }

    private static JsonObject ParseDefault()
        => JsonNode.Parse(ReadDefaultProfile())!.AsObject();

    private static LoadedHostManagerProfile Load(JsonObject profile)
        => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()),
            "test/applied-ownership.json");

    private static CompiledHostManagerPlan Compile(JsonObject profile)
        => new HostManagerPlanCompiler().Compile(
            Load(profile),
            HostManagerTestPlanFactory.CreateSettingsInput(),
            91,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));

    private static byte[] ReadDefaultProfile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
