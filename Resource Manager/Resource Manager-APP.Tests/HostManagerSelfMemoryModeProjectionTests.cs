using System.Collections.Immutable;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSelfMemoryModeProjectionTests
{
    private const ulong Generation = 7;
    private static readonly ulong SelfSoftwareKey =
        NativeStableIdentity.CreateCaseInsensitiveKey(
            RuntimeAttributionIds.ResourceManagerSelf);

    [Theory]
    [InlineData((byte)NativeMemoryMode.Unrestricted, (byte)LocalResourceSoftwareMemoryMode.Unrestricted)]
    [InlineData((byte)NativeMemoryMode.Normal, (byte)LocalResourceSoftwareMemoryMode.Normal)]
    [InlineData((byte)NativeMemoryMode.Optimize, (byte)LocalResourceSoftwareMemoryMode.Optimize)]
    [InlineData((byte)NativeMemoryMode.PagedFrozen, (byte)LocalResourceSoftwareMemoryMode.PagedFrozen)]
    public void ReadyCurrentAuthorityMapsTheSelfSoftwareMode(
        byte nativeMode,
        byte expected)
    {
        var fixture = CreateReadyFixture((NativeMemoryMode)nativeMode);

        var actual = HostManagerSelfMemoryModeProjection.Resolve(
            selfMemoryActionsEnabled: true,
            fixture.Binding,
            fixture.Authority);

        Assert.Equal((LocalResourceSoftwareMemoryMode)expected, actual);
    }

    [Fact]
    public void DisabledAdaptedActionsReturnNormal()
    {
        var fixture = CreateReadyFixture(NativeMemoryMode.Optimize);

        var actual = HostManagerSelfMemoryModeProjection.Resolve(
            selfMemoryActionsEnabled: false,
            fixture.Binding,
            fixture.Authority);

        Assert.Equal(LocalResourceSoftwareMemoryMode.Normal, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyAuthorityWithoutSelfReturnsNormal(bool includeOtherSoftware)
    {
        var fixture = CreateAbsentFixture(includeOtherSoftware);

        Assert.Equal(LocalResourceSoftwareMemoryMode.Normal,
            HostManagerSelfMemoryModeProjection.Resolve(true, fixture.Binding, fixture.Authority));
    }

    [Fact]
    public void SelfAppearingAndDisappearingUsesOnlyTheCurrentValue()
    {
        var absent = CreateAbsentFixture(false);
        var present = CreateReadyFixture(NativeMemoryMode.Optimize);

        Assert.Equal(LocalResourceSoftwareMemoryMode.Normal,
            HostManagerSelfMemoryModeProjection.Resolve(true, absent.Binding, absent.Authority));
        Assert.Equal(LocalResourceSoftwareMemoryMode.Optimize,
            HostManagerSelfMemoryModeProjection.Resolve(true, present.Binding, present.Authority));
        Assert.Equal(LocalResourceSoftwareMemoryMode.Normal,
            HostManagerSelfMemoryModeProjection.Resolve(true, absent.Binding, absent.Authority));
    }

    [Fact]
    public void NonReadyOrDifferentHostPlanReturnsNormal()
    {
        var fixture = CreateReadyFixture(NativeMemoryMode.Optimize);
        var computeOnly = fixture.Authority with
        {
            Availability = HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            MemoryModes = null,
            PolicyEvidence = null,
            UnavailableReason = "memory-source-unavailable"
        };

        Assert.Equal(
            LocalResourceSoftwareMemoryMode.Normal,
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding,
                computeOnly));
        Assert.Equal(
            LocalResourceSoftwareMemoryMode.Normal,
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding with { HostPublicationSequence = 2 },
                fixture.Authority));
    }

    [Fact]
    public void ReadyAuthorityRequiresExactlyOneBoundSelfRow()
    {
        var fixture = CreateReadyFixture(NativeMemoryMode.Optimize);
        var modes = fixture.Authority.MemoryModes!;
        var self = Assert.Single(modes.Software);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding,
                fixture.Authority with
                {
                    Compute = fixture.Authority.Compute! with
                    {
                        Cpu = fixture.Authority.Compute!.Cpu! with { Scores = [] }
                    }
                }));

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding,
                fixture.Authority with
                {
                    MemoryModes = modes with
                    {
                        Software = ImmutableArray<HostManagerDesiredMemoryMode>.Empty
                    }
                }));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding,
                fixture.Authority with
                {
                    MemoryModes = modes with
                    {
                        Software = ImmutableArray.Create(self, self)
                    }
                }));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSelfMemoryModeProjection.Resolve(
                true,
                fixture.Binding,
                fixture.Authority with
                {
                    MemoryModes = modes with
                    {
                        Software = [self with { CpuScore = self.CpuScore + 1 }]
                    }
                }));
    }

    private static Fixture CreateAbsentFixture(bool includeOtherSoftware)
    {
        var fixture = CreateReadyFixture(NativeMemoryMode.Normal);
        var compute = fixture.Authority.Compute!;
        var cpu = compute.Cpu!;
        var modes = fixture.Authority.MemoryModes!;
        var otherKey = SelfSoftwareKey ^ 1UL;
        return fixture with
        {
            Authority = fixture.Authority with
            {
                Compute = compute with
                {
                    Cpu = cpu with
                    {
                        Scores = includeOtherSoftware
                            ? [Assert.Single(cpu.Scores) with { TargetKey = otherKey, SoftwareKey = otherKey }]
                            : []
                    }
                },
                MemoryModes = modes with
                {
                    Software = includeOtherSoftware
                        ? [Assert.Single(modes.Software) with { SoftwareKey = otherKey }]
                        : []
                }
            }
        };
    }

    private static Fixture CreateReadyFixture(NativeMemoryMode mode)
    {
        var binding = new HostManagerSchedulingPlanBinding(
            1,
            10,
            new string('A', 64),
            20,
            new string('B', 64),
            true,
            HostManagerMemoryModePolicySourceKinds.ProductBaseline,
            30,
            new string('C', 64));
        const double cpuScore = 12.5;
        var score = new HostManagerComputeScore(
            NativeComputeScoringOutputKind.SoftwareCpu,
            Generation,
            SelfSoftwareKey,
            SelfSoftwareKey,
            0,
            0,
            0,
            cpuScore,
            1,
            NativeComputeScoringRuntimeState.BackgroundProcess);
        var compute = new HostManagerComputeScoringCycleResult(
            Generation,
            new(
                Generation,
                100,
                0,
                0,
                ImmutableArray.Create(score)),
            null);
        var desired = new HostManagerDesiredMemoryMode(
            SelfSoftwareKey,
            Generation,
            Generation,
            cpuScore,
            0,
            mode,
            NativeMemoryGradeSet.Normal | NativeMemoryGradeSet.L1 | NativeMemoryGradeSet.L2 | NativeMemoryGradeSet.L3,
            30);
        var memoryModes = new HostManagerMemoryModeDesiredSnapshot(
            binding.MemoryModeConfigurationGeneration,
            Generation,
            new HostManagerMemorySourceStamp(100, 200),
            Generation,
            false,
            mode == NativeMemoryMode.Optimize ? 1U : 0U,
            mode == NativeMemoryMode.PagedFrozen ? 1U : 0U,
            ImmutableArray.Create(desired));
        var evidence = HostManagerMemoryModePolicyEvidence.Create(
            binding,
            binding.MemoryModePolicySourceKind,
            binding.MemoryModeConfigurationSha256,
            allowUnrestricted: false);
        return new(
            binding,
            new(
                Generation,
                HostManagerSchedulingAuthorityAvailability.Ready,
                DateTimeOffset.UtcNow,
                binding,
                compute,
                memoryModes,
                evidence,
                null));
    }

    private sealed record Fixture(
        HostManagerSchedulingPlanBinding Binding,
        HostManagerSchedulingAuthoritySnapshot Authority);
}
