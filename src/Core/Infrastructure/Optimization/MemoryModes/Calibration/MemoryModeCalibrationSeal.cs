namespace ResourceManager.App.Infrastructure.Optimization;

internal enum MemoryModeCalibrationDomain : byte
{
    Memory = 0
}

internal sealed record MemoryModeCalibrationAcceptancePolicy(
    uint MinimumTrainingSamples,
    uint MinimumValidationSamples,
    uint MaximumTrainingMismatchPer10K,
    uint MaximumValidationMismatchPer10K);

internal sealed record MemoryModeCalibrationEvaluation(
    uint SampleCount,
    ulong WeightedMismatch,
    ulong WeightedLabelCount);

internal sealed record MemoryModeCalibrationThresholds(
    uint UnrestrictedMinimumFreeRatioUnits,
    uint NormalMinimumFreeRatioUnits,
    uint StrongBeginFreeRatioUnits);

internal sealed record MemoryModeCalibrationSeal(
    uint Version,
    MemoryModeCalibrationDomain Domain,
    ulong CorpusLength,
    string CorpusSha256,
    MemoryModeCalibrationAcceptancePolicy AcceptancePolicy,
    MemoryModeCalibrationThresholds Thresholds,
    MemoryModeCalibrationEvaluation Training,
    MemoryModeCalibrationEvaluation Validation,
    string ArtifactSha256,
    string EncodedSha256);

internal sealed class MemoryModeCalibrationSealException : Exception
{
    internal MemoryModeCalibrationSealException(string code, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A calibration seal failure code is required.", nameof(code));
        }
        Code = code;
    }

    internal string Code { get; }
}
