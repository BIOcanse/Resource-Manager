using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeDisplayCoordinatorAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0003_0000U, NativeDisplayCoordinatorAbi.Version);
        Assert.Equal(160, Unsafe.SizeOf<NativeDisplayCoordinatorConfiguration>());
        Assert.Equal(128, Unsafe.SizeOf<NativeDisplayCoordinatorCapacity>());
        Assert.Equal(80, Unsafe.SizeOf<NativeDisplayRefreshInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativeDisplaySourceBatchHeader>());
        Assert.Equal(48, Unsafe.SizeOf<NativeDisplayTextInput>());
        Assert.Equal(168, Unsafe.SizeOf<NativeDisplayFact>());
        Assert.Equal(64, Unsafe.SizeOf<NativeDisplayFinalizeInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeDisplayAbortInput>());
        Assert.Equal(56, Unsafe.SizeOf<NativeDisplayReadInput>());
        Assert.Equal(160, Unsafe.SizeOf<NativeDisplayNodeOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeDisplayEdgeOutput>());
        Assert.Equal(136, Unsafe.SizeOf<NativeDisplayCapabilityOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeDisplayTextOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativeDisplayDiffEntry>());
        Assert.Equal(80, Unsafe.SizeOf<NativeDisplayUnresolvedOutput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeDisplaySnapshotOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativeDisplayPersistenceInput>());
        Assert.Equal(184, Unsafe.SizeOf<NativeDisplayPersistenceHeader>());

        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.Generation),
            8);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.MaximumSourceCount),
            16);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.ResidentByteBudget),
            64);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.RequiredSourceMask),
            72);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.IdentitySourcePriorityOrder),
            88);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.ProjectionSourcePriorityOrder),
            112);
        AssertOffset<NativeDisplayCoordinatorConfiguration>(
            nameof(NativeDisplayCoordinatorConfiguration.Reserved),
            136);
        AssertOffset<NativeDisplayFact>(
            nameof(NativeDisplayFact.MonitorPathTextIndex),
            56);
        AssertOffset<NativeDisplayFact>(
            nameof(NativeDisplayFact.MatchTextIndex),
            76);
        AssertOffset<NativeDisplayFact>(
            nameof(NativeDisplayFact.MatchFlags),
            80);
        AssertOffset<NativeDisplayFact>(
            nameof(NativeDisplayFact.CapabilityFlags),
            128);
        AssertOffset<NativeDisplayFact>(
            nameof(NativeDisplayFact.PayloadHandle),
            152);
        AssertOffset<NativeDisplayNodeOutput>(
            nameof(NativeDisplayNodeOutput.PrimarySourceId),
            112);
        AssertOffset<NativeDisplayNodeOutput>(
            nameof(NativeDisplayNodeOutput.PrimarySourceRecordOrdinal),
            120);
        AssertOffset<NativeDisplayNodeOutput>(
            nameof(NativeDisplayNodeOutput.PrimaryPayloadHandle),
            128);
        AssertOffset<NativeDisplayNodeOutput>(
            nameof(NativeDisplayNodeOutput.OemProfilePayloadHandle),
            144);
        AssertOffset<NativeDisplaySnapshotOutput>(
            nameof(NativeDisplaySnapshotOutput.ResidentByteCount),
            120);
        AssertOffset<NativeDisplayPersistenceHeader>(
            nameof(NativeDisplayPersistenceHeader.SourceGenerations),
            96);
        AssertOffset<NativeDisplayPersistenceHeader>(
            nameof(NativeDisplayPersistenceHeader.Checksum),
            152);
    }

    [Fact]
    public void PublishedEnumsAndMasksMatchTheZigContract()
    {
        Assert.Equal(7, (int)NativeDisplayCoordinatorStatus.OutOfMemory);
        Assert.Equal(5U, (uint)NativeDisplaySource.OemConnectorProfile);
        Assert.Equal(5U, (uint)NativeDisplaySourceStatus.Unsupported);
        Assert.Equal(5U, (uint)NativeDisplayCoordinatorPhase.FailedNoData);
        Assert.Equal(0x1FUL, (ulong)NativeDisplaySourceMask.Known);
        Assert.Equal(0x0FUL, (ulong)NativeDisplayRefreshValidity.Required);
        Assert.Equal(0x1FUL, (ulong)NativeDisplayBatchValidity.Required);
        Assert.Equal((1UL << 23) - 1, (ulong)NativeDisplayFactValidity.Known);
        Assert.Equal(0x1FUL, (ulong)NativeDisplayIdentityMask.Known);
        Assert.Equal(0x3FUL, (ulong)NativeDisplayCapabilityFlags.Known);
        Assert.Equal(0x0FU, (uint)NativeDisplaySnapshotFlags.Known);
        Assert.Equal(1U, (uint)NativeDisplayOemMatchFlags.Known);
    }

    [Fact]
    public void RealNativeLibraryCreatesAndReportsExactCapacity()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeDisplayCoordinatorSession(in configuration);

        Assert.Equal(
            NativeDisplayCoordinatorAbi.Version,
            NativeDisplayCoordinatorSession.GetAbiVersion());
        Assert.Equal(
            configuration.MaximumObservationCount,
            session.Capacity.MaximumObservationCount);
        Assert.Equal(
            configuration.MaximumNodeCount,
            session.Capacity.MaximumNodeCount);
        Assert.Equal(
            configuration.MaximumDiffEntryCount,
            session.Capacity.MaximumDiffEntryCount);
        Assert.InRange(
            session.Capacity.ResidentByteCount,
            1UL,
            configuration.ResidentByteBudget);

        Assert.Equal(
            NativeDisplayCoordinatorStatus.Ok,
            session.Snapshot(out var snapshot));
        Assert.Equal(configuration.Generation, snapshot.ConfigurationGeneration);
        Assert.Equal((uint)NativeDisplayCoordinatorPhase.Warming, snapshot.Phase);
    }

    private static NativeDisplayCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize =
                checked((uint)Unsafe.SizeOf<NativeDisplayCoordinatorConfiguration>()),
            Generation = 1,
            MaximumSourceCount = NativeDisplayCoordinatorAbi.SourceCount,
            MaximumObservationCount = 8,
            MaximumNodeCount = 16,
            MaximumEdgeCount = 8,
            MaximumCapabilityCount = 8,
            MaximumDiffEntryCount = 160,
            MaximumTextBindingCount = 32,
            MaximumTextByteCount = 4096,
            MaximumUnresolvedCount = 8,
            MaximumSourceBatchCount = NativeDisplayCoordinatorAbi.SourceCount,
            IdentityIndexCapacity = 64,
            TextIndexCapacity = 32,
            ResidentByteBudget = 4 * 1024 * 1024,
            RequiredSourceMask = (ulong)NativeDisplaySourceMask.DisplayConfig,
            OptionalSourceMask = (ulong)(
                NativeDisplaySourceMask.Dxgi
                | NativeDisplaySourceMask.Edid
                | NativeDisplaySourceMask.SetupApiMonitor
                | NativeDisplaySourceMask.OemConnectorProfile),
            IdentitySourcePriorityOrder = 0x0000_0005_0403_0201UL,
            FriendlyNameSourcePriorityOrder = 0x0000_0005_0403_0201UL,
            CapabilitySourcePriorityOrder = 0x0000_0005_0403_0201UL,
            ProjectionSourcePriorityOrder = 0x0000_0005_0403_0201UL,
            IdentityContractVersion = NativeDisplayCoordinatorAbi.IdentityContractVersion,
            CapabilityContractVersion =
                NativeDisplayCoordinatorAbi.CapabilityContractVersion,
            MaximumFutureSkewMilliseconds = 30_000
        };

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
