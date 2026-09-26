using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeFileQueryAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0002_0000U, NativeFileQueryAbi.Version);
        Assert.Equal(0x0006_0100U, NativeFileQueryAbi.UnicodeTokenizerVersion);
        Assert.Equal(2U, NativeFileQueryAbi.UnicodeRemoveDiacriticsMode);
        Assert.Equal(0x0001_0000U, NativeFileQueryAbi.TrigramTokenizerContractVersion);
        Assert.Equal(0x0001_0000U, NativeFileQueryAbi.TextMatchingVersion);
        Assert.Equal(168, Unsafe.SizeOf<NativeFileQueryConfiguration>());
        Assert.Equal(104, Unsafe.SizeOf<NativeFileQueryCapacity>());
        Assert.Equal(72, Unsafe.SizeOf<NativeFileQueryBeginInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativeFileQuerySourcePlan>());
        Assert.Equal(120, Unsafe.SizeOf<NativeFileQueryPlanOutput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeFileQueryCandidateInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeFileQuerySubmitInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativeFileQuerySubmitOutput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeFileQueryFinalizeInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativeFileQueryResult>());
        Assert.Equal(80, Unsafe.SizeOf<NativeFileQueryFinalizeOutput>());
        Assert.Equal(56, Unsafe.SizeOf<NativeFileQueryResetInput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeFileQuerySnapshot>());

        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.Generation),
            8);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.MaximumQueryUtf8ByteCount),
            16);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.UnicodeTokenizerVersion),
            76);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.UnicodeRemoveDiacriticsMode),
            80);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.TrigramTokenizerContractVersion),
            84);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.TextMatchingVersion),
            116);
        AssertOffset<NativeFileQueryConfiguration>(
            nameof(NativeFileQueryConfiguration.ResidentByteBudget),
            120);
        AssertOffset<NativeFileQueryCapacity>(
            nameof(NativeFileQueryCapacity.ResidentByteCount),
            64);
        AssertOffset<NativeFileQueryBeginInput>(
            nameof(NativeFileQueryBeginInput.ValidMask),
            40);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.NormalizedQueryOffset),
            64);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.NormalizedQueryLength),
            68);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.UnicodeTokenizerVersion),
            72);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.UnicodeRemoveDiacriticsMode),
            76);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.TrigramTokenizerContractVersion),
            80);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.TextMatchingVersion),
            84);
        AssertOffset<NativeFileQueryPlanOutput>(
            nameof(NativeFileQueryPlanOutput.Flags),
            88);
        AssertOffset<NativeFileQueryCandidateInput>(
            nameof(NativeFileQueryCandidateInput.EntryHandle),
            8);
        AssertOffset<NativeFileQueryCandidateInput>(
            nameof(NativeFileQueryCandidateInput.Flags),
            48);
        AssertOffset<NativeFileQueryResult>(
            nameof(NativeFileQueryResult.EntryHandle),
            8);
        AssertOffset<NativeFileQuerySnapshot>(
            nameof(NativeFileQuerySnapshot.ResidentByteCount),
            88);
    }

    [Fact]
    public void PublishedEnumsAndMasksMatchTheZigContract()
    {
        Assert.Equal(7, (int)NativeFileQueryStatus.OutOfMemory);
        Assert.Equal(4U, (uint)NativeFileQueryPhase.Finalized);
        Assert.Equal(2U, (uint)NativeFileQueryPlanMode.Fts);
        Assert.Equal(4U, (uint)NativeFileQuerySource.ShortScan);
        Assert.Equal(1U, (uint)NativeFileQueryPrimitive.ShortSubstringOrderedScan);
        Assert.Equal(4U, (uint)NativeFileQueryPrimitive.SoftwareNameTrigramFts);
        Assert.Equal(0x0FU, (uint)NativeFileQuerySourceMask.Known);
        Assert.Equal(0x0FUL, (ulong)NativeFileQueryBeginValidity.Required);
        Assert.Equal(0x1FUL, (ulong)NativeFileQuerySubmitValidity.Required);
        Assert.Equal(0x07UL, (ulong)NativeFileQueryFinalizeValidity.Required);
        Assert.Equal(0x01UL, (ulong)NativeFileQueryResetValidity.Required);
    }

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
