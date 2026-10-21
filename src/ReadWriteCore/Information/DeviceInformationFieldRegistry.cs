namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAllLogicalBusyRatio =
        new("cpu_all_logical_busy_ratio", "cpu", "all", "number", "%", "cpu.logical_busy_time_percent", "windows.processor-time");
    public static readonly DeviceInformationDefinition CpuAllPhysicalCoreBusyRatio =
        new("cpu_all_physical_core_busy_ratio", "cpu", "all", "number", "%", "cpu.physical_core_smt_mean_busy_time_percent", "windows.processor-time");
    public static readonly DeviceInformationDefinition CpuAllSocketDesignation =
        new("cpu_all_socket_designation", "cpu", "all", "text", "text", "cpu.socket_designation_declared", "smbios.type4");
    public static readonly DeviceInformationDefinition CpuAllNumaNodeId =
        new("cpu_all_numa_node_id", "cpu", "all", "unsigned-integer", "node32", "cpu.os_numa_node_id", "windows.numa-topology");
    public static readonly DeviceInformationDefinition CpuAllNumaNodeCpuAffinity =
        new("cpu_all_numa_node_cpu_affinity", "cpu", "all", "processor-addresses", "group-index[]", "cpu.numa_node_cpu_affinity", "windows.numa-topology");
    public static readonly DeviceInformationDefinition CpuAllSmtEnabled =
        new("cpu_all_smt_enabled", "cpu", "all", "boolean", "boolean", "cpu.smt_enabled", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllPhysicalCoreCountReported =
        new("cpu_all_physical_core_count_reported", "cpu", "all", "unsigned-integer", "count", "cpu.physical_core_count_reported", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllLogicalProcessorCountReported =
        new("cpu_all_logical_processor_count_reported", "cpu", "all", "unsigned-integer", "count", "cpu.logical_processor_count_reported", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllPhysicalCoreId =
        new("cpu_all_physical_core_id", "cpu", "all", "text", "application-id", "cpu.physical_core_application_id", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllOsEfficiencyClass =
        new("cpu_all_os_efficiency_class", "cpu", "all", "unsigned-integer", "class8", "cpu.os_efficiency_class", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllCoreLogicalProcessorAddresses =
        new("cpu_all_core_logical_processor_addresses", "cpu", "all", "processor-addresses", "group-index[]", "cpu.core_logical_processor_addresses", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllSmtRelationshipReported =
        new("cpu_all_smt_relationship_reported", "cpu", "all", "boolean", "boolean", "cpu.smt_relationship_reported", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllLogicalProcessorId =
        new("cpu_all_logical_processor_id", "cpu", "all", "unsigned-integer", "application-id", "cpu.logical_processor_application_id", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllProcessorGroup =
        new("cpu_all_processor_group", "cpu", "all", "unsigned-integer", "group16", "cpu.processor_group", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllGroupRelativeProcessorIndex =
        new("cpu_all_group_relative_processor_index", "cpu", "all", "unsigned-integer", "index6", "cpu.group_relative_processor_index", "windows.processor-core");
    public static readonly DeviceInformationDefinition CpuAllCacheLevel =
        new("cpu_all_cache_level", "cpu", "all", "unsigned-integer", "level8", "cpu.cache_level", "windows.processor-cache");
    public static readonly DeviceInformationDefinition CpuAllCacheCapacity =
        new("cpu_all_cache_capacity", "cpu", "all", "unsigned-integer", "B", "cpu.cache_capacity", "windows.processor-cache");
    public static readonly DeviceInformationDefinition CpuAllCacheLineSize =
        new("cpu_all_cache_line_size", "cpu", "all", "unsigned-integer", "B", "cpu.cache_line_size", "windows.processor-cache");
    public static readonly DeviceInformationDefinition CpuAllCacheAssociativity =
        new("cpu_all_cache_associativity", "cpu", "all", "unsigned-integer", "associativity8", "cpu.cache_associativity_declared_code", "windows.processor-cache");
    public static readonly DeviceInformationDefinition CpuAllCacheType =
        new("cpu_all_cache_type", "cpu", "all", "unsigned-integer", "enum32", "cpu.cache_type", "windows.processor-cache");
    public static readonly DeviceInformationDefinition CpuAllCacheSharedLogicalProcessorIds =
        new("cpu_all_cache_shared_logical_processor_ids", "cpu", "all", "integer-list", "application-id[]", "cpu.cache_shared_logical_processor_ids", "windows.processor-cache");
    public static readonly DeviceInformationDefinition PciAllVendorId =
        new("pci_all_vendor_id", "pci", "all", "text", "hex16", "pci.vendor_id_declared", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllDeviceId =
        new("pci_all_device_id", "pci", "all", "text", "hex16", "pci.device_id_declared", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllRevisionIdDeclared =
        new("pci_all_revision_id_declared", "pci", "all", "text", "hex8", "pci.revision_id_declared", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllSubsystemIdentifierDeclared =
        new("pci_all_subsystem_identifier_declared", "pci", "all", "text", "hex16", "pci.subsystem_identifier_declared", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllSubsystemVendorIdDeclared =
        new("pci_all_subsystem_vendor_id_declared", "pci", "all", "text", "hex16", "pci.subsystem_vendor_id_declared", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllBaseClassDeclaredCode =
        new("pci_all_base_class_declared_code", "pci", "all", "text", "hex8", "pci.base_class_declared_code", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllSubclassDeclaredCode =
        new("pci_all_subclass_declared_code", "pci", "all", "text", "hex8", "pci.subclass_declared_code", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition PciAllProgrammingInterfaceDeclaredCode =
        new("pci_all_programming_interface_declared_code", "pci", "all", "text", "hex8", "pci.programming_interface_declared_code", "windows.pnp.pci-identifiers");
    public static readonly DeviceInformationDefinition BiosAllReleaseDate =
        new("bios_all_release_date", "bios", "all", "text", "date", "bios.release_date_declared", "smbios.type0");
    public static readonly DeviceInformationDefinition MotherboardAllSlotDesignation =
        new("motherboard_all_slot_designation", "motherboard", "all", "text", "text", "motherboard.slot_designation_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotTypeDeclaredCode =
        new("motherboard_all_slot_type_declared_code", "motherboard", "all", "unsigned-integer", "enum8", "motherboard.slot_type_declared_code", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotDataBusWidthDeclaredCode =
        new("motherboard_all_slot_data_bus_width_declared_code", "motherboard", "all", "unsigned-integer", "enum8", "motherboard.slot_data_bus_width_declared_code", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotCurrentUsageDeclaredCode =
        new("motherboard_all_slot_current_usage_declared_code", "motherboard", "all", "unsigned-integer", "enum8", "motherboard.slot_current_usage_declared_code", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotLengthDeclaredCode =
        new("motherboard_all_slot_length_declared_code", "motherboard", "all", "unsigned-integer", "enum8", "motherboard.slot_length_declared_code", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotIdDeclared =
        new("motherboard_all_slot_id_declared", "motherboard", "all", "unsigned-integer", "id16", "motherboard.slot_id_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotCharacteristics1DeclaredMask =
        new("motherboard_all_slot_characteristics_1_declared_mask", "motherboard", "all", "unsigned-integer", "bitmask8", "motherboard.slot_characteristics_1_declared_mask", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotCharacteristics2DeclaredMask =
        new("motherboard_all_slot_characteristics_2_declared_mask", "motherboard", "all", "unsigned-integer", "bitmask8", "motherboard.slot_characteristics_2_declared_mask", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotPciSegmentDeclared =
        new("motherboard_all_slot_pci_segment_declared", "motherboard", "all", "unsigned-integer", "id16", "motherboard.slot_pci_segment_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotPciBusDeclared =
        new("motherboard_all_slot_pci_bus_declared", "motherboard", "all", "unsigned-integer", "id8", "motherboard.slot_pci_bus_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotPciDeviceDeclared =
        new("motherboard_all_slot_pci_device_declared", "motherboard", "all", "unsigned-integer", "id5", "motherboard.slot_pci_device_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition MotherboardAllSlotPciFunctionDeclared =
        new("motherboard_all_slot_pci_function_declared", "motherboard", "all", "unsigned-integer", "id3", "motherboard.slot_pci_function_declared", "smbios.type9");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiTableSignature =
        new("firmware_all_acpi_table_signature", "firmware", "all", "text", "text", "firmware.acpi_table_signature", "windows.acpi.tables");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiSignatureInstanceCountReported =
        new("firmware_all_acpi_signature_instance_count_reported", "firmware", "all", "unsigned-integer", "count", "firmware.acpi_signature_instance_count_reported", "windows.acpi.tables");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiTableRaw =
        new("firmware_all_acpi_table_raw", "firmware", "all", "bytes", "B", "firmware.acpi_first_table_raw", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiTableRevision =
        new("firmware_all_acpi_table_revision", "firmware", "all", "unsigned-integer", "version8", "firmware.acpi_first_table_revision", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiTableLengthDeclaredBytes =
        new("firmware_all_acpi_table_length_declared_bytes", "firmware", "all", "unsigned-integer", "B", "firmware.acpi_first_table_length_declared_bytes", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiOemIdDeclared =
        new("firmware_all_acpi_oem_id_declared", "firmware", "all", "text", "text", "firmware.acpi_first_table_oem_id_declared", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiOemTableIdDeclared =
        new("firmware_all_acpi_oem_table_id_declared", "firmware", "all", "text", "text", "firmware.acpi_first_table_oem_table_id_declared", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiOemRevisionDeclared =
        new("firmware_all_acpi_oem_revision_declared", "firmware", "all", "unsigned-integer", "version32", "firmware.acpi_first_table_oem_revision_declared", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiCreatorIdDeclared =
        new("firmware_all_acpi_creator_id_declared", "firmware", "all", "text", "text", "firmware.acpi_first_table_creator_id_declared", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition FirmwareAllAcpiCreatorRevisionDeclared =
        new("firmware_all_acpi_creator_revision_declared", "firmware", "all", "unsigned-integer", "version32", "firmware.acpi_first_table_creator_revision_declared", "windows.acpi.first-table");
    public static readonly DeviceInformationDefinition BatteryAllManufacturerDeclared =
        new("battery_all_manufacturer_declared", "battery", "all", "text", "text", "battery.manufacturer_declared", "windows.battery");
    public static readonly DeviceInformationDefinition BatteryAllSerialDeclared =
        new("battery_all_serial_declared", "battery", "all", "text", "text", "battery.serial_declared", "windows.battery");
    public static readonly DeviceInformationDefinition BatteryAllChemistryDeclared =
        new("battery_all_chemistry_declared", "battery", "all", "text", "text", "battery.chemistry_declared", "windows.battery");
    public static readonly DeviceInformationDefinition MachineAllManufacturer =
        new("machine_all_manufacturer", "machine", "all", "text", "text", "machine.manufacturer", "windows.wmi.computer-system");
    public static readonly DeviceInformationDefinition MachineAllModelDeclared =
        new("machine_all_model_declared", "machine", "all", "text", "text", "machine.model_declared", "windows.wmi.computer-system");
    public static readonly DeviceInformationDefinition BiosAllVendor =
        new("bios_all_vendor", "bios", "all", "text", "text", "bios.vendor", "windows.wmi.bios");
    public static readonly DeviceInformationDefinition BiosAllVersionDeclared =
        new("bios_all_version_declared", "bios", "all", "text", "text", "bios.version_declared", "windows.wmi.bios");
    public static readonly DeviceInformationDefinition MotherboardAllManufacturer =
        new("motherboard_all_manufacturer", "motherboard", "all", "text", "text", "motherboard.manufacturer", "windows.wmi.baseboard");
    public static readonly DeviceInformationDefinition MotherboardAllModelDeclared =
        new("motherboard_all_model_declared", "motherboard", "all", "text", "text", "motherboard.model_declared", "windows.wmi.baseboard");
    public static readonly DeviceInformationDefinition MotherboardAllSerialNumber =
        new("motherboard_all_serial_number", "motherboard", "all", "text", "text", "motherboard.serial_number", "windows.wmi.baseboard");
    public static readonly DeviceInformationDefinition MemoryAllDimmCapacity =
        new("memory_all_dimm_capacity", "memory", "all", "unsigned-integer", "B", "memory.dimm_capacity", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmLocator =
        new("memory_all_dimm_locator", "memory", "all", "text", "text", "memory.dimm_locator", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmManufacturer =
        new("memory_all_dimm_manufacturer", "memory", "all", "text", "text", "memory.dimm_manufacturer", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmPartNumber =
        new("memory_all_dimm_part_number", "memory", "all", "text", "text", "memory.dimm_part_number", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmSerialNumberDeclared =
        new("memory_all_dimm_serial_number_declared", "memory", "all", "text", "text", "memory.dimm_serial_number_declared", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmTypeDeclared =
        new("memory_all_dimm_type_declared", "memory", "all", "text", "text", "memory.dimm_type_declared", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmDeclaredDataRate =
        new("memory_all_dimm_declared_data_rate", "memory", "all", "unsigned-integer", "MT/s", "memory.dimm_declared_data_rate", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmConfiguredDataRate =
        new("memory_all_dimm_configured_data_rate", "memory", "all", "unsigned-integer", "MT/s", "memory.dimm_configured_data_rate", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmTotalWidthDeclaredBits =
        new("memory_all_dimm_total_width_declared_bits", "memory", "all", "unsigned-integer", "bit", "memory.dimm_total_width_declared_bits", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmDataWidthDeclaredBits =
        new("memory_all_dimm_data_width_declared_bits", "memory", "all", "unsigned-integer", "bit", "memory.dimm_data_width_declared_bits", "smbios.type17");
    public static readonly DeviceInformationDefinition MemoryAllDimmConfiguredVoltageDeclaredVolts =
        new("memory_all_dimm_configured_voltage_declared_volts", "memory", "all", "number", "V", "memory.dimm_configured_voltage_declared_volts", "smbios.type17");
    public static readonly DeviceInformationDefinition PciAllPnpDeviceIdDeclared =
        new("pci_all_pnp_device_id_declared", "pci", "all", "text", "text", "pci.pnp_device_id_declared", "windows.pnp");
    public static readonly DeviceInformationDefinition PciAllParentDeviceId =
        new("pci_all_parent_device_id", "pci", "all", "text", "text", "pci.parent_device_id", "windows.configuration-manager");
    public static readonly DeviceInformationDefinition NetworkAllAdapterIdDeclared =
        new("network_all_adapter_id_declared", "network", "all", "text", "text", "network.pnp_device_id_declared", "windows.pnp");
    public static readonly DeviceInformationDefinition NetworkAllInterfaceNameReported =
        new("network_all_interface_name_reported", "network", "all", "text", "text", "network.interface_name_reported", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition NetworkAllLinkStateActual =
        new("network_all_link_state_actual", "network", "all", "unsigned-integer", "enum", "network.link_state_actual", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition NetworkAllTransmitLinkSpeedActualBitsPerSecond =
        new("network_all_transmit_link_speed_actual_bits_per_second", "network", "all", "unsigned-integer", "bit/s", "network.transmit_link_speed_actual_bits_per_second", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition NetworkAllReceiveLinkSpeedActualBitsPerSecond =
        new("network_all_receive_link_speed_actual_bits_per_second", "network", "all", "unsigned-integer", "bit/s", "network.receive_link_speed_actual_bits_per_second", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition NetworkAllMacAddressDeclared =
        new("network_all_mac_address_declared", "network", "all", "text", "text", "network.mac_address_declared", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition NetworkAllMtuActual =
        new("network_all_mtu_actual", "network", "all", "unsigned-integer", "B", "network.mtu_actual", "windows.wmi.netadapter");
    public static readonly DeviceInformationDefinition DisplayAllResolutionActual =
        new("display_all_resolution_actual", "display", "all", "resolution", "pixels", "display.source_mode_resolution_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllRefreshRateActual =
        new("display_all_refresh_rate_actual", "display", "all", "rational", "Hz", "display.refresh_rate_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllBitDepthActual =
        new("display_all_bit_depth_actual", "display", "all", "unsigned-integer", "bits/channel", "display.bit_depth_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllAdvancedColorSupported =
        new("display_all_advanced_color_supported", "display", "all", "boolean", "boolean", "display.advanced_color_supported", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllAdvancedColorEnabledActual =
        new("display_all_advanced_color_enabled_actual", "display", "all", "boolean", "boolean", "display.advanced_color_enabled_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllSdrWhiteLevelActualNits =
        new("display_all_sdr_white_level_actual_nits", "display", "all", "number", "nit", "display.sdr_white_level_actual_nits", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllColorEncodingActual =
        new("display_all_color_encoding_actual", "display", "all", "text", "enum", "display.color_encoding_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllMinimumLuminanceReportedNits =
        new("display_all_minimum_luminance_reported_nits", "display", "all", "number", "nit", "display.minimum_luminance_reported_nits", "windows.dxgi.output6");
    public static readonly DeviceInformationDefinition DisplayAllMaximumLuminanceReportedNits =
        new("display_all_maximum_luminance_reported_nits", "display", "all", "number", "nit", "display.maximum_luminance_reported_nits", "windows.dxgi.output6");
    public static readonly DeviceInformationDefinition DisplayAllMaximumFullFrameLuminanceReportedNits =
        new("display_all_maximum_full_frame_luminance_reported_nits", "display", "all", "number", "nit", "display.maximum_full_frame_luminance_reported_nits", "windows.dxgi.output6");
    public static readonly DeviceInformationDefinition HidAllPollIntervalTheoretical =
        new("hid_all_poll_interval_theoretical", "hid", "all", "number", "ms", "hid.poll_interval_theoretical", "usb.endpoint-descriptors");
    public static readonly DeviceInformationDefinition HidAllReportRateTheoreticalHz =
        new("hid_all_report_rate_theoretical_hz", "hid", "all", "number", "Hz", "hid.report_rate_theoretical_hz", "usb.endpoint-descriptors");
    public static readonly DeviceInformationDefinition UsbAllManufacturerDeclared =
        new("usb_all_manufacturer_declared", "usb", "all", "text", "text", "usb.manufacturer_declared", "usb.string-descriptors");
    public static readonly DeviceInformationDefinition UsbAllProductNameDeclared =
        new("usb_all_product_name_declared", "usb", "all", "text", "text", "usb.product_name_declared", "usb.string-descriptors");
    public static readonly DeviceInformationDefinition UsbAllSerialNumberDeclared =
        new("usb_all_serial_number_declared", "usb", "all", "text", "text", "usb.serial_number_declared", "usb.string-descriptors");
    public static readonly DeviceInformationDefinition CameraAllUvcModeCapability =
        new("camera_all_uvc_mode_capability", "camera", "all", "camera-modes", "mode", "camera.uvc_mode_capability", "usb.video-class-descriptors");
    public static readonly DeviceInformationDefinition StorageAllModelDeclared =
        new("storage_all_model_declared", "storage", "all", "text", "text", "storage.model_declared", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllSerialDeclared =
        new("storage_all_serial_declared", "storage", "all", "text", "text", "storage.serial_declared", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllFirmwareVersionDeclared =
        new("storage_all_firmware_version_declared", "storage", "all", "text", "text", "storage.firmware_version_declared", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllCapacityDeclared =
        new("storage_all_capacity_declared", "storage", "all", "unsigned-integer", "B", "storage.capacity_declared", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaLogicalSectorSizeDeclaredBytes =
        new("storage_all_ata_logical_sector_size_declared_bytes", "storage", "all", "unsigned-integer", "B", "storage.ata_logical_sector_size_declared_bytes", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaPhysicalSectorSizeDeclaredBytes =
        new("storage_all_ata_physical_sector_size_declared_bytes", "storage", "all", "unsigned-integer", "B", "storage.ata_physical_sector_size_declared_bytes", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaTrimSupported =
        new("storage_all_ata_trim_supported", "storage", "all", "boolean", "boolean", "storage.ata_trim_supported", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaMajorVersionSupportedMask =
        new("storage_all_ata_major_version_supported_mask", "storage", "all", "unsigned-integer", "bitmask16", "storage.ata_major_version_supported_mask", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaMediaRotationRateDeclaredRpm =
        new("storage_all_ata_media_rotation_rate_declared_rpm", "storage", "all", "unsigned-integer", "RPM", "storage.ata_media_rotation_rate_declared_rpm", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaNonRotatingMediaDeclared =
        new("storage_all_ata_non_rotating_media_declared", "storage", "all", "boolean", "boolean", "storage.ata_non_rotating_media_declared", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllAtaIdentifyRaw =
        new("storage_all_ata_identify_raw", "storage", "all", "bytes", "B", "storage.ata_identify_raw", "windows.ata-identify");
    public static readonly DeviceInformationDefinition StorageAllNvmeSubsystemModelDeclared =
        new("storage_all_nvme_subsystem_model_declared", "storage", "all", "text", "text", "storage.nvme_subsystem_model_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeSubsystemSerialDeclared =
        new("storage_all_nvme_subsystem_serial_declared", "storage", "all", "text", "text", "storage.nvme_subsystem_serial_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeSubsystemFirmwareVersionDeclared =
        new("storage_all_nvme_subsystem_firmware_version_declared", "storage", "all", "text", "text", "storage.nvme_subsystem_firmware_version_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerVendorIdDeclared =
        new("storage_all_nvme_controller_vendor_id_declared", "storage", "all", "unsigned-integer", "id", "storage.nvme_controller_vendor_id_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerSubsystemVendorIdDeclared =
        new("storage_all_nvme_controller_subsystem_vendor_id_declared", "storage", "all", "unsigned-integer", "id", "storage.nvme_controller_subsystem_vendor_id_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerIeeeOuiDeclared =
        new("storage_all_nvme_controller_ieee_oui_declared", "storage", "all", "unsigned-integer", "id", "storage.nvme_controller_ieee_oui_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerVersionDeclared =
        new("storage_all_nvme_controller_version_declared", "storage", "all", "unsigned-integer", "version32", "storage.nvme_controller_version_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerIdDeclared =
        new("storage_all_nvme_controller_id_declared", "storage", "all", "unsigned-integer", "id", "storage.nvme_controller_id_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerAccessibleTotalNvmCapacityReportedBytes =
        new("storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.nvme_controller_accessible_total_nvm_capacity_reported_bytes", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeControllerAccessibleUnallocatedNvmCapacityReportedBytes =
        new("storage_all_nvme_controller_accessible_unallocated_nvm_capacity_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.nvme_controller_accessible_unallocated_nvm_capacity_reported_bytes", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeSubsystemMaximumValidNamespaceIdDeclared =
        new("storage_all_nvme_subsystem_maximum_valid_namespace_id_declared", "storage", "all", "unsigned-integer", "id", "storage.nvme_subsystem_maximum_valid_namespace_id_declared", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeDatasetManagementSupported =
        new("storage_all_nvme_dataset_management_supported", "storage", "all", "boolean", "boolean", "storage.nvme_dataset_management_supported", "windows.nvme-identify-controller");
    public static readonly DeviceInformationDefinition StorageAllNvmeIdentifyControllerRaw =
        new("storage_all_nvme_identify_controller_raw", "storage", "all", "bytes", "B", "storage.nvme_identify_controller_raw", "windows.nvme-identify-controller");
}
