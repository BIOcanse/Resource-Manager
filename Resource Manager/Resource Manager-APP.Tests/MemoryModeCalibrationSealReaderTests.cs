using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class MemoryModeCalibrationSealReaderTests
{
    internal const string MemoryEncodedSha256 =
        "84D9DD542A17D410D95C38DEF72E667841AC452401E2B5C4492179F1D192B354";
    internal const string LegacyVramEncodedSha256 =
        "279285EF28AED8FBB31BABDC0C0A315129693E8584DBCE9DAD0CBE35132B566D";

    private const string MemoryBase64 =
        "Uk1DQUwwMDEBAAAAAAAAADsCAAAAAAAAlIyDXTg3IekmPr/ogb7PHnz/WsMnHPcGoCd5jIdsqGMIAAAACAAAAAAAAAAAAAAAcBcAALgLAADoAwAACAAAAAAAAAAAAAAAGAAAAAAAAAAIAAAAAAAAAAAAAAAYAAAAAAAAAM6MBZCMO/hYnRGF4+7I09IWFzxvXgcVfazMQcE54YlG";
    private const string LegacyVramBase64 =
        "Uk1DQUwwMDEBAAAAAQAAAFQBAAAAAAAAHNstl7S/l/qNtYvlkibKoYaeNMi7sd9p9yrw9Wu7dyIEAAAABAAAAAAAAAAAAAAAcBcAALgLAADoAwAABAAAAAAAAAAAAAAADAAAAAAAAAAEAAAAAAAAAAAAAAAMAAAAAAAAAFigcS7AjHWYTcT1eVBFIpE1noGe+SMTDBNBydN/ebcu";

    [Fact]
    public void DecodesExactNativeCoreMemorySeal()
    {
        var memory = MemoryModeCalibrationSealReader.Decode(
            MemoryBytes,
            MemoryEncodedSha256);

        Assert.Equal(MemoryModeCalibrationDomain.Memory, memory.Domain);
        Assert.Equal(6000U, memory.Thresholds.UnrestrictedMinimumFreeRatioUnits);
        Assert.Equal(3000U, memory.Thresholds.NormalMinimumFreeRatioUnits);
        Assert.Equal(1000U, memory.Thresholds.StrongBeginFreeRatioUnits);
        Assert.Equal(8U, memory.Training.SampleCount);
        Assert.Equal(
            "948C835D383721E9263EBFE881BECF1E7CFF5AC3271CF706A027798C876CA863",
            memory.CorpusSha256);
        Assert.Equal(
            "CE8C05908C3BF8589D1185E3EEC8D3D216173C6F5E07157DACCC41C139E18946",
            memory.ArtifactSha256);
    }

    [Fact]
    public void RejectsLegacyVramDomainBeforePublishingThresholds()
    {
        var failure = Assert.Throws<MemoryModeCalibrationSealException>(() =>
            MemoryModeCalibrationSealReader.Decode(
                LegacyVramBytes,
                LegacyVramEncodedSha256));

        Assert.Equal("domain-invalid", failure.Code);
    }

    [Fact]
    public void RejectsTamperedFieldsEvenWhenTheOuterHashIsRepinned()
    {
        var bytes = MemoryBytes;
        bytes[72] ^= 1;
        var repinned = Convert.ToHexString(SHA256.HashData(bytes));

        var failure = Assert.Throws<MemoryModeCalibrationSealException>(() =>
            MemoryModeCalibrationSealReader.Decode(bytes, repinned));

        Assert.Equal("artifact-sha256-mismatch", failure.Code);
    }

    [Fact]
    public void RejectsASealWhoseOwnAcceptancePolicyNoLongerAcceptsIt()
    {
        var bytes = MemoryBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 9);
        var repinned = Convert.ToHexString(SHA256.HashData(bytes));

        var failure = Assert.Throws<MemoryModeCalibrationSealException>(() =>
            MemoryModeCalibrationSealReader.Decode(bytes, repinned));

        Assert.Equal("acceptance-invalid", failure.Code);
    }

    [Fact]
    public void RejectsNoncanonicalExpectedIdentityAndWrongLength()
    {
        var identityFailure = Assert.Throws<MemoryModeCalibrationSealException>(() =>
            MemoryModeCalibrationSealReader.Decode(
                MemoryBytes,
                MemoryEncodedSha256.ToLowerInvariant()));
        Assert.Equal("expected-sha256-invalid", identityFailure.Code);

        var lengthFailure = Assert.Throws<MemoryModeCalibrationSealException>(() =>
            MemoryModeCalibrationSealReader.Decode(
                MemoryBytes[..^1],
                MemoryEncodedSha256));
        Assert.Equal("length-invalid", lengthFailure.Code);
    }

    internal static byte[] MemoryBytes => Convert.FromBase64String(MemoryBase64);

    internal static byte[] LegacyVramBytes => Convert.FromBase64String(LegacyVramBase64);
}
