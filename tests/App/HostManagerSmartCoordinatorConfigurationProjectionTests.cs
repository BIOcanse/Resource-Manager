using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartCoordinatorConfigurationProjectionTests
{
    [Fact]
    public void CompiledPlanProjectsEveryNativeConfigurationSection()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SmartCoordinator;

        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(
            plan.Build,
            plan.Recreate,
            plan.HotPublish,
            plan.ConfigurationGeneration);

        Assert.Equal(NativeSmartCoordinatorAbi.Version, configuration.AbiVersion);
        Assert.Equal(264u, configuration.StructSize);
        Assert.Equal(plan.ConfigurationGeneration, configuration.Generation);
        Assert.Equal(NativeSmartCoordinatorConfigFields.Required, configuration.FieldMask);
        Assert.Equal((NativeSmartCoordinatorFeatures)plan.HotPublish.FeatureFlags, configuration.FeatureFlags);
        Assert.Equal((uint)plan.Recreate.MaximumProcesses, configuration.MaximumProcesses);
        Assert.Equal((uint)plan.Recreate.MaximumSoftwareGroups, configuration.MaximumSoftwareGroups);
        Assert.Equal((uint)plan.Recreate.MaximumGpuStates, configuration.MaximumGpuStates);
        Assert.Equal((uint)plan.Recreate.MaximumInputRows, configuration.MaximumInputRows);
        Assert.Equal((uint)plan.Recreate.MaximumActions, configuration.MaximumActions);
        Assert.Equal((uint)plan.Recreate.MaximumReservations, configuration.MaximumReservations);
        Assert.Equal((uint)plan.Recreate.MaximumAtomicGroups, configuration.MaximumAtomicGroups);
        Assert.Equal((uint)plan.HotPublish.NormalIntervalMilliseconds, configuration.NormalIntervalMilliseconds);
        Assert.Equal((uint)plan.HotPublish.EventIntervalMilliseconds, configuration.EventIntervalMilliseconds);
        Assert.Equal((uint)plan.HotPublish.EventBoostRounds, configuration.EventBoostRounds);
        Assert.Equal((uint)plan.HotPublish.GameStartGraceMilliseconds, configuration.GameStartGraceMilliseconds);
        Assert.Equal((uint)plan.HotPublish.RequiredConsecutiveDecisions, configuration.RequiredConsecutiveDecisions);
        Assert.Equal((uint)plan.HotPublish.FailureRetryMilliseconds, configuration.FailureRetryMilliseconds);
        Assert.Equal((uint)plan.HotPublish.ReservationTimeoutMilliseconds, configuration.ReservationTimeoutMilliseconds);
        Assert.Equal(plan.HotPublish.A1MinimumCpuScore, configuration.A1MinimumCpuScore);
        Assert.Equal(plan.HotPublish.DefaultMinimumCpuScoreScale, configuration.DefaultMinimumCpuScoreScale);
        Assert.Equal(plan.HotPublish.Level1MaximumCpuScoreScale, configuration.Level1MaximumCpuScoreScale);
        Assert.Equal(plan.HotPublish.Level2MaximumCpuScoreScale, configuration.Level2MaximumCpuScoreScale);
        Assert.Equal(plan.HotPublish.Level3MaximumCpuScoreScale, configuration.Level3MaximumCpuScoreScale);
        Assert.Equal(0, configuration.LowTierLevel4MaximumCpuScoreScale);
        Assert.Equal(0, configuration.ReservedProcessPolicy0);
        Assert.Equal(plan.HotPublish.BaseScoreTiers.HighMinimumBaseScore, configuration.HighTierMinimumBaseScore);
        Assert.Equal(plan.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore, configuration.MiddleTierMinimumBaseScore);

        var projected = configuration;
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref projected, 1));
        for (var index = 0; index < NativeSmartCoordinatorAbi.RuntimeStateCount; index++)
        {
            Assert.Equal(plan.HotPublish.ProcessStateMultipliers[index], ReadDouble(bytes, 104 + (index * sizeof(double))));
        }
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(88 + (index * sizeof(uint)), sizeof(uint))));
        }
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(232 + (index * sizeof(ulong)), sizeof(ulong))));
        }

        Assert.Equal(
            plan.ConfigurationSha256,
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(
                in configuration,
                plan.HotPublish.RealtimeActionBudget));
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            NativeSmartCoordinatorSession.CapacityForConfiguration(in configuration, out var capacity));
        Assert.Equal(plan.ConfigurationGeneration, capacity.ConfigurationGeneration);
    }

    [Theory]
    [InlineData("percent", 32, 5)]
    [InlineData("fixed", 33, 5)]
    [InlineData("fixed", 32, 6)]
    public void ConfigurationIdentityIncludesEveryRealtimeBudgetFieldWithUnchangedNativeConfiguration(
        string mode,
        int count,
        int percent)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SmartCoordinator;
        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(
            plan.Build,
            plan.Recreate,
            plan.HotPublish,
            plan.ConfigurationGeneration);
        var baseline = new CompiledHostManagerRealtimeActionBudgetPlan("fixed", 32, 5);
        var changed = new CompiledHostManagerRealtimeActionBudgetPlan(mode, count, percent);

        Assert.NotEqual(
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(in configuration, baseline),
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(in configuration, changed));
    }

    [Fact]
    public void ConfigurationIdentityIncludesEventBoostRoundsAtTheSameGeneration()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().SmartCoordinator;
        var original = NativeSmartCoordinatorConfigurationWriter.Create(
            plan.Build, plan.Recreate, plan.HotPublish, plan.ConfigurationGeneration);
        var changed = NativeSmartCoordinatorConfigurationWriter.Create(
            plan.Build, plan.Recreate, plan.HotPublish with { EventBoostRounds = 11 }, plan.ConfigurationGeneration);
        Assert.Equal(10U, original.EventBoostRounds);
        Assert.Equal(11U, changed.EventBoostRounds);
        Assert.NotEqual(
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(in original, plan.HotPublish.RealtimeActionBudget),
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(in changed, plan.HotPublish.RealtimeActionBudget));
    }

    private static double ReadDouble(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(double))));
}
