using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTransactionJournalConfigurationTests
{
    [Fact]
    public void DefaultProfile_ProjectsCompleteTransactionJournalAuthority()
    {
        var plan = Compile(ReadDefaultProfile());

        Assert.Equal(0x0005_0000U, plan.BuildSpecialize.TransactionJournalAbiVersion);
        Assert.Equal(65_536, plan.BuildSpecialize.CapacityLimits.TransactionJournalRecordCapacity);
        Assert.Equal(268_435_456L, plan.BuildSpecialize.CapacityLimits.TransactionJournalResidentByteBudget);
        Assert.Equal(65_536, plan.BuildSpecialize.CapacityLimits.TransactionJournalPayloadCount);
        Assert.Equal(1_073_741_824L, plan.BuildSpecialize.CapacityLimits.TransactionJournalPayloadByteBudget);
        Assert.Single(plan.BuildSpecialize.NativeModules, static module => module == "transaction_journal");
        Assert.Contains(
            "transaction_journal",
            plan.BuildSpecialize.NativeBinaries
                .Single(static binary => binary.FileName == "ResourceManager.NativeCore.dll")
                .Modules);

        Assert.True(plan.HostRecreate.TransactionJournal.IsPublished);
        Assert.Equal(8192, plan.HostRecreate.TransactionJournal.RecordCapacity);
        Assert.Equal(33_554_432L, plan.HostRecreate.TransactionJournal.ResidentByteBudget);
        Assert.Equal(8192, plan.HostRecreate.TransactionJournal.PayloadCount);
        Assert.Equal(268_435_456L, plan.HostRecreate.TransactionJournal.PayloadByteBudget);
        Assert.Equal(
            "host-manager/transaction-journal/journal.bin",
            plan.HostRecreate.TransactionJournal.JournalRelativePath);
        Assert.Equal(
            "host-manager/transaction-journal/payloads",
            plan.HostRecreate.TransactionJournal.PayloadRelativeDirectory);

        Assert.True(plan.HotPublish.TransactionJournal.IsPublished);
        Assert.Equal((byte)10, (byte)HostManagerModuleKind.TransactionJournal);
        Assert.Equal(59UL << 32, plan.HotPublish.TransactionJournal.ConfigurationGeneration);
        Assert.Equal(3, plan.HotPublish.TransactionJournal.MaximumRecoveryAttempts);
        Assert.Equal(5000, plan.HotPublish.TransactionJournal.RetryDelayMilliseconds);
        Assert.Equal(300000, plan.HotPublish.TransactionJournal.RecoveryDeadlineMilliseconds);
        Assert.Equal(5000, plan.HotPublish.TransactionJournal.MaximumFutureSkewMilliseconds);
        Assert.Equal(30000, plan.HotPublish.TransactionJournal.ShutdownDrainTimeoutMilliseconds);
        Assert.True(plan.DeploymentDigests.TransactionJournal.IsPublished);
    }

    [Fact]
    public void Compiler_RejectsOldOrIncompleteTransactionJournalShape()
    {
        var oldSchema = ParseDefault();
        oldSchema["schema_version"] = 9;
        Assert.Throws<InvalidDataException>(() => Compile(oldSchema));

        var missingRecreate = ParseDefault();
        missingRecreate["host_recreate"]!.AsObject().Remove("transaction_journal");
        Assert.Throws<InvalidDataException>(() => Load(missingRecreate));

        var missingHot = ParseDefault();
        missingHot["hot_publish"]!.AsObject().Remove("transaction_journal");
        Assert.Throws<InvalidDataException>(() => Load(missingHot));

        var missingAbi = ParseDefault();
        missingAbi["build_specialize"]!.AsObject().Remove("transaction_journal_abi_version");
        Assert.Throws<InvalidDataException>(() => Load(missingAbi));

        var missingBuildLimit = ParseDefault();
        missingBuildLimit["build_specialize"]!["capacity_limits"]!
            .AsObject()
            .Remove("transaction_journal_payload_byte_budget");
        Assert.Throws<InvalidDataException>(() => Load(missingBuildLimit));

        var missingPath = ParseDefault();
        missingPath["host_recreate"]!["transaction_journal"]!
            .AsObject()
            .Remove("journal_relative_path");
        Assert.Throws<InvalidDataException>(() => Load(missingPath));

        var missingModule = ParseDefault();
        missingModule["build_specialize"]!["native_modules"] = new JsonArray(
            missingModule["build_specialize"]!["native_modules"]!
                .AsArray()
                .Where(static item => item?.GetValue<string>() != "transaction_journal")
                .Select(static item => item?.DeepClone())
                .ToArray());
        Assert.Throws<InvalidDataException>(() => Compile(missingModule));
    }

    [Fact]
    public void Compiler_RejectsTransactionJournalCapacityAboveBuildLimit()
    {
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["record_capacity"] = 65_537);
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["resident_byte_budget"] = 268_435_457L);
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["payload_count"] = 65_537);
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["payload_byte_budget"] = 1_073_741_825L);
        AssertRejected(static profile =>
            profile["build_specialize"]!["transaction_journal_abi_version"] = 0x0003_0000U);
    }

    [Fact]
    public void Compiler_RejectsEscapingOrOverlappingTransactionJournalPaths()
    {
        string[] invalidJournalPaths =
        [
            string.Empty,
            "C:/state/journal.bin",
            "C:state/journal.bin",
            "/state/journal.bin",
            "\\\\server\\share\\journal.bin",
            "state//journal.bin",
            "state\\\\journal.bin",
            "state/./journal.bin",
            "state/../journal.bin",
            "state/"
        ];
        foreach (var path in invalidJournalPaths)
        {
            AssertRejected(profile =>
                profile["host_recreate"]!["transaction_journal"]!["journal_relative_path"] = path);
        }

        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["payload_relative_directory"] =
                "host-manager/transaction-journal");
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["payload_relative_directory"] =
                "host-manager/./payloads");
        AssertRejected(static profile =>
            profile["host_recreate"]!["transaction_journal"]!["payload_relative_directory"] =
                "host-manager/transaction-journal/journal.bin");
        AssertRejected(static profile =>
        {
            profile["host_recreate"]!["transaction_journal"]!["journal_relative_path"] = "state";
            profile["host_recreate"]!["transaction_journal"]!["payload_relative_directory"] =
                "state/payloads";
        });
    }

    [Fact]
    public void Compiler_UsesRevisionGenerationAndModuleSpecificDigests()
    {
        var first = Compile(ReadDefaultProfile());
        var changedHotProfile = ParseDefault();
        changedHotProfile["hot_publish"]!["transaction_journal"]!["retry_delay_ms"] = 6000;
        var changedHot = Compile(changedHotProfile);

        Assert.Equal(
            first.HotPublish.TransactionJournal.ConfigurationGeneration,
            changedHot.HotPublish.TransactionJournal.ConfigurationGeneration);
        Assert.Equal(
            first.DeploymentDigests.TransactionJournal.RecreateSha256,
            changedHot.DeploymentDigests.TransactionJournal.RecreateSha256);
        Assert.NotEqual(
            first.DeploymentDigests.TransactionJournal.HotPublishSha256,
            changedHot.DeploymentDigests.TransactionJournal.HotPublishSha256);

        changedHotProfile["profile_revision"] = first.ProfileRevision + 1;
        var nextRevision = Compile(changedHotProfile);
        Assert.Equal((ulong)(first.ProfileRevision + 1) << 32, nextRevision.HotPublish.TransactionJournal.ConfigurationGeneration);
        Assert.Equal(0U, unchecked((uint)nextRevision.HotPublish.TransactionJournal.ConfigurationGeneration));

        var changedRecreateProfile = ParseDefault();
        changedRecreateProfile["host_recreate"]!["transaction_journal"]!["record_capacity"] = 4096;
        var changedRecreate = Compile(changedRecreateProfile);
        Assert.NotEqual(
            first.DeploymentDigests.TransactionJournal.RecreateSha256,
            changedRecreate.DeploymentDigests.TransactionJournal.RecreateSha256);
        Assert.Equal(
            first.DeploymentDigests.TransactionJournal.HotPublishSha256,
            changedRecreate.DeploymentDigests.TransactionJournal.HotPublishSha256);
    }

    [Fact]
    public void DeploymentState_TracksTransactionJournalAsOneExactModule()
    {
        var plan = Compile(ReadDefaultProfile());
        var state = new HostManagerDeploymentState();
        state.PublishRuntimePlan(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "transaction-journal-test",
            HostManager = plan
        });

        var pending = state.Snapshot.TransactionJournal;
        Assert.Equal(HostManagerModuleKind.TransactionJournal, pending.Module);
        Assert.Equal(HostManagerPendingLifecycle.InitialCreate, pending.PendingLifecycle);
        Assert.Equal(
            plan.DeploymentDigests.TransactionJournal.RecreateSha256,
            pending.DesiredRecreateSha256);
        Assert.Equal(
            plan.DeploymentDigests.TransactionJournal.HotPublishSha256,
            pending.DesiredHotPublishSha256);

        var token = state.BeginAttempt(
            HostManagerModuleKind.TransactionJournal,
            plan,
            HostManagerDeploymentOperation.InitialCreate);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            state.CompleteAttemptSucceeded(token));
        Assert.Equal(HostManagerDeploymentStatus.InSync, state.Snapshot.TransactionJournal.Status);
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
            "test/transaction-journal.json");

    private static CompiledHostManagerPlan Compile(JsonObject profile)
        => Compile(Encoding.UTF8.GetBytes(profile.ToJsonString()));

    private static CompiledHostManagerPlan Compile(byte[] profile)
        => new HostManagerPlanCompiler().Compile(
            StrictHostManagerProfileLoader.LoadBytes(profile, "test/transaction-journal.json"),
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
