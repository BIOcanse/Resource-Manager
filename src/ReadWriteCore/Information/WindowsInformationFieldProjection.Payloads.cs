// Explicit field payloads preserve the original property names and order.
namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private sealed record AtaSmartThresholdSlotValue(
        byte slotIndex,
        byte attributeId,
        byte threshold,
        byte[] reservedTenBytes);

    private sealed record AtaSmartThresholdPageValue(
        ushort revision,
        AtaSmartThresholdSlotValue[] slots);

    private sealed record AtaSmartRawSlotValue(
        byte slotIndex,
        byte attributeId,
        byte[] rawSixBytes);

    private sealed record AtaSmartRawPageValue(
        ushort revision,
        AtaSmartRawSlotValue[] slots);

    private sealed record AtaSmartNormalizedSlotValue(
        byte slotIndex,
        byte attributeId,
        ushort flags,
        byte currentNormalized,
        byte worstNormalized,
        byte reservedByte);

    private sealed record AtaSmartNormalizedPageValue(
        ushort revision,
        AtaSmartNormalizedSlotValue[] slots);

    private sealed record D3dDecoderConfigurationValue(
        uint index,
        global::ResourceManager.ReadWrite.Windows.Devices.Gpu.WindowsD3dVideoDecoderConfig configuration);

    private sealed record D3dDecoderDescriptorValue(
        string profileGuid,
        uint sampleWidth,
        uint sampleHeight,
        uint outputFormat);

    private sealed record D3dDecoderConfigurationsValue(
        D3dDecoderDescriptorValue descriptor,
        uint configurationCount,
        D3dDecoderConfigurationValue[] configurations);

    private sealed record D3dFrameRateValue(
        uint numerator,
        uint denominator);

    private sealed record D3dContentDescriptorValue(
        uint inputFrameFormat,
        D3dFrameRateValue inputFrameRate,
        uint inputWidth,
        uint inputHeight,
        D3dFrameRateValue outputFrameRate,
        uint outputWidth,
        uint outputHeight,
        uint usage);

    private sealed record D3dRateConversionValue(
        uint index,
        global::ResourceManager.ReadWrite.Windows.Devices.Gpu.WindowsD3dRateConversionCapsValue caps);

    private sealed record D3dRateConversionsValue(
        object contentDescriptor,
        uint rateConversionCapsCount,
        D3dRateConversionValue[] rateConversionCaps);

    private sealed record D3dProcessorCapabilitiesValue(
        object contentDescriptor,
        global::ResourceManager.ReadWrite.Windows.Devices.Gpu.WindowsD3dVideoProcessorCapsValue? caps);

    private sealed record NvidiaDrsBinaryPayload(
        string kind,
        int length,
        string base64);

    private sealed record NvidiaDrsStringPayload(
        string kind,
        string value);

    private sealed record NvidiaDrsDwordPayload(
        string kind,
        uint value);

    private sealed record NvidiaDrsSettingValue(
        string profileName,
        uint settingId,
        string? settingName,
        uint? settingTypeRaw,
        uint? settingLocationRaw,
        uint? isCurrentPredefinedRaw,
        uint? isPredefinedValidRaw,
        uint? profileGpuSupportRaw,
        uint? profileIsPredefinedRaw,
        object? currentValue);

}
