using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerFileQueryConfigurationTests
{
    [Fact]
    public void DefaultProfilePublishesExactFileQueryPlan()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var plan = hostPlan.FileQuery;

        Assert.True(plan.IsPublished);
        Assert.Equal(NativeFileQueryAbi.Version, plan.Build.AbiVersion);
        Assert.Equal("file_query", plan.Build.NativeModule);
        Assert.Equal(64, plan.Build.MaximumQuerySessionCount);
        Assert.Equal(1_073_741_824L, plan.Build.MaximumTotalResidentByteBudget);
        Assert.Equal(8, plan.Recreate.QuerySessionCount);
        Assert.Equal(4096, plan.Recreate.Capacity.MaximumQueryUtf8ByteCount);
        Assert.Equal(1024, plan.Recreate.Capacity.MaximumQueryRuneCount);
        Assert.Equal(3, plan.Recreate.Capacity.MaximumSourcePlanCount);
        Assert.Equal(1000, plan.Recreate.Capacity.MaximumCandidateCountPerSource);
        Assert.Equal(3000, plan.Recreate.Capacity.MaximumSubmittedCandidateCount);
        Assert.Equal(3000, plan.Recreate.Capacity.MaximumUniqueCandidateCount);
        Assert.Equal(128, plan.Recreate.Capacity.MaximumCandidateSubmitBatchCount);
        Assert.Equal(200, plan.Recreate.Capacity.MaximumResultCount);
        Assert.Equal(4096, plan.Recreate.Capacity.EntryIndexCapacity);
        Assert.Equal(4096, plan.Recreate.Capacity.OrdinalIndexCapacity);
        Assert.Equal(59UL << 32, plan.ConfigurationGeneration);
        Assert.Equal(3, plan.HotPublish.ShortQueryRuneThreshold);
        Assert.Equal(
            NativeFileQueryAbi.UnicodeTokenizerVersion,
            plan.HotPublish.UnicodeTokenizerVersion);
        Assert.Equal(
            NativeFileQueryAbi.UnicodeRemoveDiacriticsMode,
            plan.HotPublish.UnicodeRemoveDiacriticsMode);
        Assert.Equal(
            NativeFileQueryAbi.TrigramTokenizerContractVersion,
            plan.HotPublish.TrigramTokenizerContractVersion);
        Assert.Equal(
            NativeFileQueryAbi.TextMatchingVersion,
            plan.HotPublish.TextMatchingVersion);
        Assert.Equal(8, plan.HotPublish.CandidateLimitMultiplier);
        Assert.Equal(128, plan.HotPublish.CandidateLimitFloor);
        Assert.Equal(1000, plan.HotPublish.CandidateLimitCeiling);
        Assert.Equal(12_582_912L, plan.HotPublish.PerSessionResidentByteBudget);
        Assert.Equal(100_663_296L, plan.HotPublish.TotalResidentByteBudget);
        Assert.True(hostPlan.DeploymentDigests.FileQuery.IsPublished);
        Assert.Equal((byte)17, (byte)HostManagerModuleKind.FileQuery);
    }

    [Fact]
    public void CompilerRejectsMissingOrInvalidFileQueryShape()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["build_specialize"]!.AsObject().Remove("file_query_abi_version")));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["build_specialize"]!["capacity_limits"]!["file_query"]!["per_session"]!
                .AsObject()
                .Remove("ordinal_index_capacity")));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["file_query"]!["query_session_count"] = 65));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["file_query"]!["per_session"]!
                ["maximum_submitted_candidate_count"] = 2999));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["file_query"]!["unicode_tokenizer_version"] = 0x000F_0100));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["file_query"]!["total_resident_byte_budget"] = 12_582_912));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["file_query"]!["candidate_limit_multiplier"] = int.MaxValue));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var modules = root["build_specialize"]!["native_modules"]!.AsArray();
            modules.Remove(
                modules.Single(static value => value?.GetValue<string>() == "file_query"));
        }));
    }

    [Fact]
    public void RuntimeSettlesOnlyTheExactFileQueryAttempt()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var runtime = new HostManagerFileQueryRuntime(provider, deployment);

        Assert.Equal(
            hostPlan.FileQuery.ConfigurationSha256,
            runtime.CaptureDesired().ConfigurationSha256);
        runtime.CompleteSucceeded(runtime.BeginInitialCreate(hostPlan));

        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            deployment.Snapshot.FileQuery.Status);
        Assert.Equal(
            hostPlan.DeploymentDigests.FileQuery.HotPublishSha256,
            deployment.Snapshot.FileQuery.AppliedHotPublishSha256);
        Assert.Equal(
            HostManagerDeploymentStatus.Initializing,
            deployment.Snapshot.ReportCoordinator.Status);
    }

    [Fact]
    public void FileQueryDigestAndGenerationChangeOnlyThroughExplicitInputs()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var sameRevision = HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["file_query"]!["candidate_limit_floor"] = 129);

        Assert.Equal(
            baseline.FileQuery.ConfigurationGeneration,
            sameRevision.FileQuery.ConfigurationGeneration);
        Assert.NotEqual(
            baseline.FileQuery.ConfigurationSha256,
            sameRevision.FileQuery.ConfigurationSha256);
        Assert.NotEqual(
            baseline.DeploymentDigests.FileQuery.HotPublishSha256,
            sameRevision.DeploymentDigests.FileQuery.HotPublishSha256);
        Assert.Equal(
            baseline.DeploymentDigests.ReportCoordinator.HotPublishSha256,
            sameRevision.DeploymentDigests.ReportCoordinator.HotPublishSha256);

        var nextRevision = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
            root["hot_publish"]!["file_query"]!["candidate_limit_floor"] = 129;
        });
        Assert.Equal((ulong)(baseline.ProfileRevision + 1) << 32, nextRevision.FileQuery.ConfigurationGeneration);
        Assert.Equal(0U, unchecked((uint)nextRevision.FileQuery.ConfigurationGeneration));
    }
}
