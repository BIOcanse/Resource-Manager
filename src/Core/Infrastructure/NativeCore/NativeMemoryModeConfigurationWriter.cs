namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeMemoryModeConfigurationWriter
{
    public static NativeMemoryModeConfiguration Create(
        ulong generation,
        uint maximumSoftwareCount,
        uint ratioUnitsMaximum,
        uint unrestrictedMinimumFreeRatioUnits,
        uint normalMinimumFreeRatioUnits,
        uint strongBeginFreeRatioUnits,
        double middleTierMinimumBaseScore,
        double highTierMinimumBaseScore)
    {
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        ArgumentOutOfRangeException.ThrowIfZero(maximumSoftwareCount);
        return new NativeMemoryModeConfiguration
        {
            AbiVersion = NativeMemoryModeControllerAbi.Version,
            StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeConfiguration>(),
            Generation = generation,
            MaximumSoftwareCount = maximumSoftwareCount,
            MaximumOutputCount = maximumSoftwareCount,
            RatioUnitsMaximum = ratioUnitsMaximum,
            UnrestrictedMinimumFreeRatioUnits = unrestrictedMinimumFreeRatioUnits,
            NormalMinimumFreeRatioUnits = normalMinimumFreeRatioUnits,
            StrongBeginFreeRatioUnits = strongBeginFreeRatioUnits,
            MiddleTierMinimumBaseScore = middleTierMinimumBaseScore,
            HighTierMinimumBaseScore = highTierMinimumBaseScore
        };
    }
}
