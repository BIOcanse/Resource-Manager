namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAmdVbiosVersion =
        new("gpu_amd_vbios_version", "gpu", "amd", "text", "text", "gpu.vbios_version_declared", "amd.adlx");
    public static readonly DeviceInformationDefinition GpuAmdVramType =
        new("gpu_amd_vram_type", "gpu", "amd", "text", "text", "gpu.vram_type_declared", "amd.adlx");
    public static readonly DeviceInformationDefinition GpuAmdVbiosPartNumber =
        new("gpu_amd_vbios_part_number", "gpu", "amd", "text", "text", "gpu.vbios_part_number_declared", "amd.adlx");
    public static readonly DeviceInformationDefinition GpuAmdVbiosDateRaw =
        new("gpu_amd_vbios_date_raw", "gpu", "amd", "text", "text", "gpu.vbios_date_text_declared", "amd.adlx");
    public static readonly DeviceInformationDefinition GpuAllAdapterLuid =
        new("gpu_all_adapter_luid", "gpu", "all", "text", "hex64", "gpu.associated_windows_adapter_luid", "windows.gpu-adapter-order");
    public static readonly DeviceInformationDefinition GpuAllName =
        new("gpu_all_name", "gpu", "all", "text", "text", "gpu.single_member_windows_adapter_name", "windows.gpu-adapter-order");
    public static readonly DeviceInformationDefinition GpuAllPciVendorId =
        new("gpu_all_pci_vendor_id", "gpu", "all", "text", "hex16", "gpu.physical_pci_vendor_id", "windows.gpu-adapter-order");
    public static readonly DeviceInformationDefinition GpuAllPciDeviceId =
        new("gpu_all_pci_device_id", "gpu", "all", "text", "hex16", "gpu.physical_pci_device_id", "windows.gpu-adapter-order");
    public static readonly DeviceInformationDefinition GpuAllDriverVersion =
        new("gpu_all_driver_version", "gpu", "all", "text", "text", "gpu.installed_inf_driver_version", "windows.gpu-adapter-order");
    public static readonly DeviceInformationDefinition GpuNvidiaVbiosVersion =
        new("gpu_nvidia_vbios_version", "gpu", "nvidia", "text", "text", "gpu.vbios_version_declared", "nvidia.nvml");
    public static readonly DeviceInformationDefinition GpuNvidiaCudaComputeCapability =
        new("gpu_nvidia_cuda_compute_capability", "gpu", "nvidia", "text", "text", "gpu.cuda_compute_capability_declared", "nvidia.nvml");
}
