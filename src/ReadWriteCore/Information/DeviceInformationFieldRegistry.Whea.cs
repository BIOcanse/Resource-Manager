namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition WheaAllEventTimeReported =
        new("whea_all_event_time_reported", "whea_record", "all", "text", "UTC",
            "whea.logger_event_time_created_utc_not_cper_timestamp", "windows.whea-logger", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition WheaAllRawRecordReported =
        new("whea_all_raw_record_reported", "whea_record", "all", "bytes", "B",
            "whea.complete_cper_raw_record_reported", "windows.whea-logger", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition WheaAllSeverityReported =
        new("whea_all_severity_reported", "whea_record", "all", "unsigned-integer", "enum",
            "whea.cper_header_severity_0_recoverable_1_fatal_2_corrected_3_informational", "windows.whea-logger", ReadVolatility.Fixed);
}
