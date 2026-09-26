using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class MemoryModeCalibrationSealReader
{
    internal const int EncodedLength = 156;
    internal const uint SchemaVersion = 1;
    internal const uint RatioUnitsMaximum = 10_000;
    internal const ulong CorpusLengthMaximum = 8UL * 1024 * 1024;

    private static readonly byte[] ArtifactDigestNamespace =
        Encoding.ASCII.GetBytes("rm-memory-mode-calibration-seal-v1");

    internal static MemoryModeCalibrationSeal ReadFile(
        string path,
        string expectedEncodedSha256)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw Failure("path-invalid", "The calibration seal path must be absolute.");
        }

        var expectedDigest = ParseCanonicalSha256(expectedEncodedSha256);
        var bytes = new byte[EncodedLength];
        using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            EncodedLength,
            FileOptions.SequentialScan))
        {
            if (stream.Length != EncodedLength)
            {
                throw Failure(
                    "length-invalid",
                    $"The calibration seal must be exactly {EncodedLength} bytes.");
            }
            stream.ReadExactly(bytes);
            if (stream.Position != EncodedLength || stream.ReadByte() != -1)
            {
                throw Failure("length-invalid", "The calibration seal changed while it was read.");
            }
        }

        return Decode(bytes, expectedDigest);
    }

    internal static MemoryModeCalibrationSeal Decode(
        ReadOnlySpan<byte> bytes,
        string expectedEncodedSha256)
        => Decode(bytes, ParseCanonicalSha256(expectedEncodedSha256));

    private static MemoryModeCalibrationSeal Decode(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> expectedEncodedDigest)
    {
        if (bytes.Length != EncodedLength)
        {
            throw Failure(
                "length-invalid",
                $"The calibration seal must be exactly {EncodedLength} bytes.");
        }

        var encodedDigest = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(encodedDigest, expectedEncodedDigest))
        {
            throw Failure(
                "encoded-sha256-mismatch",
                "The calibration seal does not match its configured encoded SHA-256.");
        }
        if (!bytes[..8].SequenceEqual("RMCAL001"u8))
        {
            throw Failure("magic-invalid", "The calibration seal magic is invalid.");
        }
        if (bytes[13] != 0 || bytes[14] != 0 || bytes[15] != 0)
        {
            throw Failure("reserved-invalid", "The calibration seal reserved bytes are nonzero.");
        }

        var version = ReadUInt32(bytes, 8);
        if (version != SchemaVersion)
        {
            throw Failure("version-unsupported", "The calibration seal schema version is unsupported.");
        }
        if (bytes[12] != (byte)MemoryModeCalibrationDomain.Memory)
        {
            throw Failure("domain-invalid", "The calibration seal domain is invalid.");
        }
        var domain = (MemoryModeCalibrationDomain)bytes[12];

        var corpusLength = ReadUInt64(bytes, 16);
        if (corpusLength is 0 or > CorpusLengthMaximum)
        {
            throw Failure("corpus-length-invalid", "The calibration corpus length is invalid.");
        }

        var acceptance = new MemoryModeCalibrationAcceptancePolicy(
            ReadUInt32(bytes, 56),
            ReadUInt32(bytes, 60),
            ReadUInt32(bytes, 64),
            ReadUInt32(bytes, 68));
        var thresholds = new MemoryModeCalibrationThresholds(
            ReadUInt32(bytes, 72),
            ReadUInt32(bytes, 76),
            ReadUInt32(bytes, 80));
        var training = ReadEvaluation(bytes, 84);
        var validation = ReadEvaluation(bytes, 104);
        ValidateThresholds(thresholds);
        ValidateAcceptance(acceptance, training, validation);

        var corpusDigest = bytes.Slice(24, 32).ToArray();
        var artifactDigest = bytes.Slice(124, 32).ToArray();
        var expectedArtifactDigest = ComputeArtifactDigest(
            version,
            domain,
            corpusLength,
            corpusDigest,
            acceptance,
            thresholds,
            training,
            validation);
        if (!CryptographicOperations.FixedTimeEquals(
                artifactDigest,
                expectedArtifactDigest))
        {
            throw Failure(
                "artifact-sha256-mismatch",
                "The calibration seal internal artifact digest is invalid.");
        }

        return new MemoryModeCalibrationSeal(
            version,
            domain,
            corpusLength,
            Convert.ToHexString(corpusDigest),
            acceptance,
            thresholds,
            training,
            validation,
            Convert.ToHexString(artifactDigest),
            Convert.ToHexString(encodedDigest));
    }

    private static byte[] ComputeArtifactDigest(
        uint version,
        MemoryModeCalibrationDomain domain,
        ulong corpusLength,
        ReadOnlySpan<byte> corpusDigest,
        MemoryModeCalibrationAcceptancePolicy acceptance,
        MemoryModeCalibrationThresholds thresholds,
        MemoryModeCalibrationEvaluation training,
        MemoryModeCalibrationEvaluation validation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ArtifactDigestNamespace);
        AppendUInt32(hash, version);
        hash.AppendData([(byte)domain]);
        AppendUInt64(hash, corpusLength);
        hash.AppendData(corpusDigest);
        AppendUInt32(hash, acceptance.MinimumTrainingSamples);
        AppendUInt32(hash, acceptance.MinimumValidationSamples);
        AppendUInt32(hash, acceptance.MaximumTrainingMismatchPer10K);
        AppendUInt32(hash, acceptance.MaximumValidationMismatchPer10K);
        AppendUInt32(hash, thresholds.UnrestrictedMinimumFreeRatioUnits);
        AppendUInt32(hash, thresholds.NormalMinimumFreeRatioUnits);
        AppendUInt32(hash, thresholds.StrongBeginFreeRatioUnits);
        AppendEvaluation(hash, training);
        AppendEvaluation(hash, validation);
        return hash.GetHashAndReset();
    }

    private static void ValidateThresholds(MemoryModeCalibrationThresholds value)
    {
        if (value.StrongBeginFreeRatioUnits == 0
            || value.StrongBeginFreeRatioUnits >= value.NormalMinimumFreeRatioUnits
            || value.NormalMinimumFreeRatioUnits >= value.UnrestrictedMinimumFreeRatioUnits
            || value.UnrestrictedMinimumFreeRatioUnits > RatioUnitsMaximum)
        {
            throw Failure("thresholds-invalid", "The calibration thresholds are not ordered.");
        }
    }

    private static void ValidateAcceptance(
        MemoryModeCalibrationAcceptancePolicy policy,
        MemoryModeCalibrationEvaluation training,
        MemoryModeCalibrationEvaluation validation)
    {
        if (policy.MinimumTrainingSamples == 0
            || policy.MinimumValidationSamples == 0
            || policy.MaximumTrainingMismatchPer10K > RatioUnitsMaximum
            || policy.MaximumValidationMismatchPer10K > RatioUnitsMaximum
            || training.SampleCount < policy.MinimumTrainingSamples
            || validation.SampleCount < policy.MinimumValidationSamples
            || !WithinMismatchRate(training, policy.MaximumTrainingMismatchPer10K)
            || !WithinMismatchRate(validation, policy.MaximumValidationMismatchPer10K))
        {
            throw Failure("acceptance-invalid", "The calibration seal does not satisfy its acceptance policy.");
        }
    }

    private static bool WithinMismatchRate(
        MemoryModeCalibrationEvaluation evaluation,
        uint maximumPer10K)
        => evaluation.WeightedLabelCount != 0
            && (UInt128)evaluation.WeightedMismatch * RatioUnitsMaximum
                <= (UInt128)evaluation.WeightedLabelCount * maximumPer10K;

    private static MemoryModeCalibrationEvaluation ReadEvaluation(
        ReadOnlySpan<byte> bytes,
        int offset)
        => new(
            ReadUInt32(bytes, offset),
            ReadUInt64(bytes, offset + 4),
            ReadUInt64(bytes, offset + 12));

    private static void AppendEvaluation(
        IncrementalHash hash,
        MemoryModeCalibrationEvaluation evaluation)
    {
        AppendUInt32(hash, evaluation.SampleCount);
        AppendUInt64(hash, evaluation.WeightedMismatch);
        AppendUInt64(hash, evaluation.WeightedLabelCount);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));

    private static byte[] ParseCanonicalSha256(string value)
    {
        if (value is null
            || value.Length != 64
            || value.Any(static character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw Failure(
                "expected-sha256-invalid",
                "The expected calibration seal SHA-256 is not canonical.");
        }
        return Convert.FromHexString(value);
    }

    private static MemoryModeCalibrationSealException Failure(
        string code,
        string message)
        => new(code, message);
}
