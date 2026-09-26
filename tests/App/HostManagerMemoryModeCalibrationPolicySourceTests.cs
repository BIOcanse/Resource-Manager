using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryModeCalibrationPolicySourceTests
{
    [Fact]
    public void DefaultProfileCreatesOneInlineBoundNativePolicy()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var binding = CreateBinding(plan, publicationSequence: 1);

        var capture = new ProductBaselineHostManagerMemoryModePolicySource()
            .Capture(binding, plan.SmartCoordinator);

        var policy = Assert.IsType<HostManagerMemoryModePolicy>(capture.Policy);
        Assert.Null(capture.UnavailableReason);
        Assert.Equal(HostManagerMemoryModePolicySourceKinds.ProductBaseline,
            policy.SourceKind);
        Assert.Equal(10_000U, policy.Configuration.RatioUnitsMaximum);
        Assert.Equal(1_000U, policy.Configuration.StrongBeginFreeRatioUnits);
        Assert.Equal(3_000U, policy.Configuration.NormalMinimumFreeRatioUnits);
        Assert.Equal(6_000U, policy.Configuration.UnrestrictedMinimumFreeRatioUnits);
        Assert.Equal(3U, policy.OptimizeMemoryPriority);
        Assert.Equal(1U, policy.PagedFrozenMemoryPriority);
        Assert.Equal(HostManagerMemoryPriorityForeignDispositions.Reject,
            policy.ForeignMemoryPriorityDisposition);
        Assert.Equal(1U, policy.OwnedStateVerificationIntervalCycles);
        Assert.True(policy.PolicyEvidence.IsBoundTo(
            binding,
            allowUnrestricted: true));
    }

    [Theory]
    [InlineData(21, 81, 20.999, 4, 31)]
    [InlineData(21, 81, 21, 3, 15)]
    [InlineData(21, 81, 30, 3, 15)]
    [InlineData(21, 81, 80.999, 3, 15)]
    [InlineData(21, 81, 81, 2, 1)]
    [InlineData(21, 81, 95, 2, 1)]
    [InlineData(31, 81, 30, 4, 31)]
    [InlineData(21, 30, 30, 2, 1)]
    public void CompiledSharedBaseScoreTiersReachNativeMemoryEligibility(
        double middleMinimum, double highMinimum, double baseScore, int expectedMode, byte expectedMask)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var tiers = root["hot_publish"]!["smart_coordinator"]!["base_score_tiers"]!;
            tiers["middle_minimum_base_score"] = middleMinimum;
            tiers["high_minimum_base_score"] = highMinimum;
        });
        Assert.Equal(middleMinimum, plan.SmartCoordinator.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore);
        Assert.Equal(highMinimum, plan.SmartCoordinator.HotPublish.BaseScoreTiers.HighMinimumBaseScore);
        var binding = CreateBinding(plan, publicationSequence: 1);
        var policy = Assert.IsType<HostManagerMemoryModePolicy>(
            new ProductBaselineHostManagerMemoryModePolicySource()
                .Capture(binding, plan.SmartCoordinator).Policy);
        var configuration = policy.Configuration;
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            new NativeMemoryModeSoftwareInput
            {
                StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
                ValidMask = NativeMemoryModeSoftwareValidity.Required,
                SoftwareKey = 1,
                SchedulingGeneration = 10,
                CpuScore = 0,
                BaseScore = baseScore,
                SourceIndex = 0
            }
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[1];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = new NativeMemoryModeGenerationEnvelope
        {
            AbiVersion = NativeMemoryModeControllerAbi.Version,
            StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeGenerationEnvelope>(),
            SoftwareInputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
            SoftwareOutputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeDesiredSoftwareOutput>(),
            ConfigurationGeneration = policy.Configuration.Generation,
            SchedulingGeneration = 10,
            MemorySourceWorkspaceIdentity = 1,
            MemorySourceCommittedGeneration = 20,
            SnapshotGeneration = 30,
            ValidMask = NativeMemoryModeEnvelopeValidity.Required,
            Flags = NativeMemoryModeEnvelopeFlags.Required |
                NativeMemoryModeEnvelopeFlags.AllowUnrestricted,
            MemoryFreeRatioUnits = 0,
            SoftwareCount = 1,
            OutputCapacity = 1
        };

        Assert.Equal(NativeMemoryModeControllerStatus.Ok,
            session.Plan(in envelope, software, outputs, ref snapshot));
        var row = Assert.Single(outputs);
        Assert.Equal((NativeMemoryMode)expectedMode, row.Mode);
        Assert.Equal(expectedMask, (byte)row.BaseScoreAllowedGrades);
        Assert.Equal(expectedMode == (int)NativeMemoryMode.Optimize ? 1U : 0U, snapshot.OptimizeCount);
        Assert.Equal(expectedMode == (int)NativeMemoryMode.PagedFrozen ? 1U : 0U, snapshot.StrongestCount);
        Assert.Equal(1UL, snapshot.MemorySourceWorkspaceIdentity);
        Assert.Equal(20UL, snapshot.MemorySourceCommittedGeneration);
    }

    [Fact]
    public void DisabledPolicyIsExplicitlyUnavailable()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root => Disable(Policy(root)));
        var capture = new ProductBaselineHostManagerMemoryModePolicySource()
            .Capture(CreateBinding(plan, 1), plan.SmartCoordinator);

        Assert.Null(capture.Policy);
        Assert.Equal("memory-mode-policy-disabled", capture.UnavailableReason);
    }

    [Fact]
    public void SourceRejectsABindingFromAnotherPublication()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var binding = CreateBinding(plan, 1) with
        {
            MemoryModeConfigurationSha256 = new string('A', 64)
        };

        Assert.Throws<InvalidDataException>(() =>
            new ProductBaselineHostManagerMemoryModePolicySource()
                .Capture(binding, plan.SmartCoordinator));
    }

    private static HostManagerSchedulingPlanBinding CreateBinding(
        CompiledHostManagerPlan plan,
        ulong publicationSequence)
    {
        var smart = plan.SmartCoordinator;
        var policy = smart.HotPublish.MemoryModePolicy;
        return new(
            publicationSequence,
            plan.PlanEpoch,
            plan.PlanSha256,
            smart.ConfigurationGeneration,
            smart.ConfigurationSha256,
            policy.Enabled,
            policy.SourceKind,
            policy.ConfigurationGeneration,
            policy.ConfigurationSha256);
    }

    private static JsonObject Policy(JsonObject root)
        => root["hot_publish"]!["smart_coordinator"]!
            ["memory_mode_policy"]!.AsObject();

    private static void Disable(JsonObject policy)
    {
        policy["enabled"] = false;
        policy["source_kind"] = string.Empty;
        policy["ratio_units_maximum"] = 0;
        policy["strong_begin_free_ratio_units"] = 0;
        policy["normal_minimum_free_ratio_units"] = 0;
        policy["unrestricted_minimum_free_ratio_units"] = 0;
        policy["allow_unrestricted"] = false;
        policy["foreign_memory_priority_disposition"] = string.Empty;
        policy["owned_state_verification_interval_cycles"] = 0;
    }
}
