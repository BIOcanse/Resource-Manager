using System.Text.Json.Serialization;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record FirmwareMemoryModule(
    string DeviceIdentity,
    [property: JsonPropertyName("memory_all_dimm_capacity")] ulong? CapacityBytes,
    [property: JsonPropertyName("memory_all_dimm_locator")] string? Locator,
    [property: JsonPropertyName("memory_all_dimm_manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("memory_all_dimm_part_number")] string? PartNumber,
    [property: JsonPropertyName("memory_all_dimm_serial_number_declared")] string? SerialNumber,
    [property: JsonPropertyName("memory_all_dimm_type_declared")] string? MemoryType,
    [property: JsonPropertyName("memory_all_dimm_declared_data_rate")] uint? DeclaredDataRateMtps,
    [property: JsonPropertyName("memory_all_dimm_configured_data_rate")] uint? ConfiguredDataRateMtps,
    [property: JsonPropertyName("memory_all_dimm_total_width_declared_bits")] ushort? TotalWidthBits,
    [property: JsonPropertyName("memory_all_dimm_data_width_declared_bits")] ushort? DataWidthBits,
    [property: JsonPropertyName("memory_all_dimm_configured_voltage_declared_volts")] double? ConfiguredVoltageVolts,
    string Source = "SMBIOS Type 17");
