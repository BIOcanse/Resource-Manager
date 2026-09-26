using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativePortableSoftwareRegistryAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0001_0000U, NativePortableSoftwareRegistryAbi.Version);
        Assert.Equal(112, Unsafe.SizeOf<NativePortableSoftwareRegistryConfiguration>());
        Assert.Equal(88, Unsafe.SizeOf<NativePortableSoftwareRegistryCapacity>());
        Assert.Equal(80, Unsafe.SizeOf<NativePortableSoftwareImportInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativePortableSoftwarePersistedPathInput>());
        Assert.Equal(144, Unsafe.SizeOf<NativePortableSoftwareObserveInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativePortableSoftwareConfirmRootInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativePortableSoftwareMarkMissingInput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePortableSoftwarePlanPersistenceInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativePortableSoftwarePersistenceOperation>());
        Assert.Equal(96, Unsafe.SizeOf<NativePortableSoftwarePersistencePlanOutput>());
        Assert.Equal(64, Unsafe.SizeOf<NativePortableSoftwarePersistenceFeedbackInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativePortableSoftwarePersistenceFeedback>());
        Assert.Equal(72, Unsafe.SizeOf<NativePortableSoftwareSnapshotInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativePortableSoftwareRegistrationSnapshot>());
        Assert.Equal(80, Unsafe.SizeOf<NativePortableSoftwarePathSnapshot>());
        Assert.Equal(144, Unsafe.SizeOf<NativePortableSoftwareSnapshotOutput>());

        AssertOffset<NativePortableSoftwareRegistryConfiguration>(
            nameof(NativePortableSoftwareRegistryConfiguration.Generation),
            8);
        AssertOffset<NativePortableSoftwareRegistryConfiguration>(
            nameof(NativePortableSoftwareRegistryConfiguration.ResidentByteBudget),
            64);
        AssertOffset<NativePortableSoftwareRegistryCapacity>(
            nameof(NativePortableSoftwareRegistryCapacity.ResidentByteCount),
            48);
        AssertOffset<NativePortableSoftwareImportInput>(nameof(NativePortableSoftwareImportInput.ValidMask), 48);
        AssertOffset<NativePortableSoftwarePersistedPathInput>(
            nameof(NativePortableSoftwarePersistedPathInput.ExecutablePathOffset),
            64);
        AssertOffset<NativePortableSoftwareObserveInput>(nameof(NativePortableSoftwareObserveInput.ValidMask), 112);
        AssertOffset<NativePortableSoftwareConfirmRootInput>(
            nameof(NativePortableSoftwareConfirmRootInput.ValidMask),
            56);
        AssertOffset<NativePortableSoftwareMarkMissingInput>(
            nameof(NativePortableSoftwareMarkMissingInput.ValidMask),
            48);
        AssertOffset<NativePortableSoftwarePlanPersistenceInput>(
            nameof(NativePortableSoftwarePlanPersistenceInput.ValidMask),
            32);
        AssertOffset<NativePortableSoftwarePersistenceOperation>(
            nameof(NativePortableSoftwarePersistenceOperation.MutationVersion),
            8);
        AssertOffset<NativePortableSoftwarePersistencePlanOutput>(
            nameof(NativePortableSoftwarePersistencePlanOutput.FirstMutationVersion),
            48);
        AssertOffset<NativePortableSoftwarePersistenceFeedbackInput>(
            nameof(NativePortableSoftwarePersistenceFeedbackInput.ValidMask),
            32);
        AssertOffset<NativePortableSoftwarePersistenceFeedback>(
            nameof(NativePortableSoftwarePersistenceFeedback.MutationVersion),
            8);
        AssertOffset<NativePortableSoftwareSnapshotInput>(nameof(NativePortableSoftwareSnapshotInput.ValidMask), 40);
        AssertOffset<NativePortableSoftwareRegistrationSnapshot>(
            nameof(NativePortableSoftwareRegistrationSnapshot.FirstObservedUtcMilliseconds),
            40);
        AssertOffset<NativePortableSoftwarePathSnapshot>(
            nameof(NativePortableSoftwarePathSnapshot.FirstObservedUtcMilliseconds),
            40);
        AssertOffset<NativePortableSoftwareSnapshotOutput>(
            nameof(NativePortableSoftwareSnapshotOutput.ResidentByteCount),
            120);
    }

    [Fact]
    public void PublishedMasksAndStatusesMatchTheZigContract()
    {
        Assert.Equal(0x03U, (uint)NativePortableSoftwarePathFlags.Known);
        Assert.Equal(0x07U, (uint)NativePortableSoftwarePersistenceFlags.Known);
        Assert.Equal(0x0FU, (uint)NativePortableSoftwareSnapshotFlags.Known);
        Assert.Equal(0x03U, (uint)NativePortableSoftwarePlanOutputFlags.Known);
        Assert.Equal(0x07U, (uint)NativePortableSoftwareSnapshotOutputFlags.Known);
        Assert.Equal(0x1FUL, (ulong)NativePortableSoftwareImportValidity.Required);
        Assert.Equal(0x3FUL, (ulong)NativePortableSoftwareObserveValidity.Required);
        Assert.Equal(0x0FUL, (ulong)NativePortableSoftwareConfirmRootValidity.Required);
        Assert.Equal(0x0FUL, (ulong)NativePortableSoftwareMarkMissingValidity.Required);
        Assert.Equal(0x03UL, (ulong)NativePortableSoftwarePlanValidity.Required);
        Assert.Equal(0x03UL, (ulong)NativePortableSoftwareFeedbackValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativePortableSoftwareSnapshotValidity.Required);
        Assert.Equal(7, (int)NativePortableSoftwareRegistryStatus.OutOfMemory);
    }

    [Fact]
    public void RootDllExportsCreateAndCapacityRoundTrip()
    {
        var configuration = new NativePortableSoftwareRegistryConfiguration
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativePortableSoftwareRegistryConfiguration>(),
            Generation = 1,
            MaximumRegistrationCount = 4,
            MaximumPathCount = 8,
            MaximumPersistenceOperationCount = 8,
            MaximumRegistrationSnapshotCount = 4,
            MaximumPathSnapshotCount = 8,
            MaximumExecutablePathByteCount = 128,
            MaximumRootPathByteCount = 128,
            RegistrationIndexCapacity = 8,
            PathIndexCapacity = 16,
            MaximumFutureSkewMilliseconds = 1000,
            ResidentByteBudget = 1_000_000
        };

        Assert.Equal(
            NativePortableSoftwareRegistryAbi.Version,
            NativePortableSoftwareRegistrySession.GetAbiVersion());
        using var session = new NativePortableSoftwareRegistrySession(in configuration);
        Assert.Equal(4U, session.Capacity.RegistrationCapacity);
        Assert.Equal(8U, session.Capacity.PathCapacity);
        Assert.InRange(session.Capacity.ResidentByteCount, 1UL, configuration.ResidentByteBudget);
    }

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
