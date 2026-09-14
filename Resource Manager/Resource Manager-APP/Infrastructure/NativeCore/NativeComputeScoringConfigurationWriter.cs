namespace ResourceManager.App.Infrastructure.NativeCore;

internal static unsafe class NativeComputeScoringConfigurationWriter
{
    public static NativeComputeScoringConfiguration Create(
        ulong generation,
        uint maximumProcessCount,
        uint maximumGpuRowCount,
        uint maximumOutputCount,
        ReadOnlySpan<double> cpuStateMultipliers,
        ReadOnlySpan<double> gpuStateMultipliers,
        double maximumBaseImportance,
        double maximumPolicyMultiplier,
        double cpuBaselineRatio,
        uint cpuCoreCount,
        double welfareUtilizationBaselinePercent)
    {
        var configuration = new NativeComputeScoringConfiguration
        {
            AbiVersion = NativeComputeScoringAbi.Version,
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringConfiguration>(),
            Generation = generation,
            FieldMask = NativeComputeScoringConfigFields.Required,
            MaximumProcessCount = maximumProcessCount,
            MaximumGpuRowCount = maximumGpuRowCount,
            MaximumOutputCount = maximumOutputCount,
            CpuBaselineRatio = cpuBaselineRatio,
            WelfareUtilizationBaselinePercent = welfareUtilizationBaselinePercent,
            CpuCoreCount = cpuCoreCount,
            MaximumBaseImportance = maximumBaseImportance,
            MaximumPolicyMultiplier = maximumPolicyMultiplier
        };
        SetCpuStateMultipliers(ref configuration, cpuStateMultipliers);
        SetGpuStateMultipliers(ref configuration, gpuStateMultipliers);
        return configuration;
    }

    public static void SetCpuStateMultipliers(
        ref NativeComputeScoringConfiguration configuration,
        ReadOnlySpan<double> values)
    {
        RequireCompleteStateVector(values);
        fixed (NativeComputeScoringConfiguration* target = &configuration)
        {
            for (var index = 0; index < values.Length; index++)
            {
                target->CpuStateMultipliers[index] = values[index];
            }
        }
    }

    public static void SetGpuStateMultipliers(
        ref NativeComputeScoringConfiguration configuration,
        ReadOnlySpan<double> values)
    {
        RequireCompleteStateVector(values);
        fixed (NativeComputeScoringConfiguration* target = &configuration)
        {
            for (var index = 0; index < values.Length; index++)
            {
                target->GpuStateMultipliers[index] = values[index];
            }
        }
    }

    private static void RequireCompleteStateVector(ReadOnlySpan<double> values)
    {
        if (values.Length != NativeComputeScoringAbi.RuntimeStateCount)
        {
            throw new ArgumentException(
                $"Exactly {NativeComputeScoringAbi.RuntimeStateCount} state multipliers are required.",
                nameof(values));
        }
    }
}
