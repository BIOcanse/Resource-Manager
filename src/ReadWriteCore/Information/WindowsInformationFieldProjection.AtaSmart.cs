using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> AtaSmartAttributeFields(
        IReadOnlyDictionary<MetricFieldCode, byte[]>? pages,
        AtaSmartAttributeInformationFields selectedFields,
        DateTimeOffset? observedAt)
    {
        const AtaSmartAttributeInformationFields all = AtaSmartAttributeInformationFields.Normalized
            | AtaSmartAttributeInformationFields.Raw | AtaSmartAttributeInformationFields.Threshold;
        if ((selectedFields & ~all) != 0) throw new ArgumentOutOfRangeException(nameof(selectedFields));

        AtaSmartAttributeDataPage? data = null;
        if ((selectedFields & (AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw)) != 0
            && pages is not null && pages.TryGetValue(MetricFieldCode.AtaSmartDataRaw, out var dataBytes))
            data = WindowsAtaSmartReader.ParseAttributeDataPage(dataBytes);

        AtaSmartAttributeThresholdPage? thresholds = null;
        if ((selectedFields & AtaSmartAttributeInformationFields.Threshold) != 0
            && pages is not null && pages.TryGetValue(MetricFieldCode.AtaSmartThresholdsRaw, out var thresholdBytes))
            thresholds = WindowsAtaSmartReader.ParseAttributeThresholdPage(thresholdBytes);

        JsonElement? normalized = (selectedFields & AtaSmartAttributeInformationFields.Normalized) != 0 && data is not null
            ? JsonSerializer.SerializeToElement(new AtaSmartNormalizedPageValue(data.Revision,data.Slots.Select(static slot => new AtaSmartNormalizedSlotValue(slot.SlotIndex,slot.AttributeId,slot.Flags,slot.CurrentNormalized,slot.WorstNormalized,slot.ReservedByte
)).ToArray()
), JsonOptions)
            : null;
        JsonElement? raw = (selectedFields & AtaSmartAttributeInformationFields.Raw) != 0 && data is not null
            ? JsonSerializer.SerializeToElement(new AtaSmartRawPageValue(data.Revision,data.Slots.Select(static slot => new AtaSmartRawSlotValue(slot.SlotIndex,slot.AttributeId,slot.RawSixBytes
)).ToArray()
), JsonOptions)
            : null;
        JsonElement? threshold = (selectedFields & AtaSmartAttributeInformationFields.Threshold) != 0 && thresholds is not null
            ? JsonSerializer.SerializeToElement(new AtaSmartThresholdPageValue(thresholds.Revision,thresholds.Slots.Select(static slot => new AtaSmartThresholdSlotValue(slot.SlotIndex,slot.AttributeId,slot.Threshold,slot.ReservedTenBytes
)).ToArray()
), JsonOptions)
            : null;

        return [
            Timed(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized, normalized, observedAt),
            Timed(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw, raw, observedAt),
            Timed(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold, threshold, observedAt)
        ];
    }

    private static DeviceInformationField Timed(DeviceInformationDefinition definition, JsonElement? value,
        DateTimeOffset? observedAt)
        => new(definition, value) { ObservedAt = value is null ? null : observedAt };
}
