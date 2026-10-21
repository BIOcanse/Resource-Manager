namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition PrinterAllDriverVersionDeclared =
        new("printer_all_driver_version_declared", "printer", "all", "text", "text",
            "printer.driver_info_6_declared_unsigned_driver_version_decimal_no_component_split", "windows.print-spooler.driver-info-6");

    public static readonly DeviceInformationDefinition PrinterAllPortDeclared =
        new("printer_all_port_declared", "printer", "all", "text", "text",
            "printer.printer_info_2_declared_port_name_or_comma_separated_port_names", "windows.print-spooler.get-printer");

    public static readonly DeviceInformationDefinition PrinterAllQueueStateActual =
        new("printer_all_queue_state_actual", "printer", "all", "unsigned-integer", "enum",
            "printer.printer_info_2_status_exact_uint32_printer_status_flags_bitmask_including_unknown_bits", "windows.print-spooler.get-printer");
}
