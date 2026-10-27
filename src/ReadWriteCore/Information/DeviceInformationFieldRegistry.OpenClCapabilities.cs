namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllOpenClDeviceVersion =
        new("gpu_all_opencl_device_version", "gpu_adapter", "all", "text", "text",
            "gpu.opencl_device_version_string_reported_by_the_exact_luid_node_matched_device",
            "opencl.clGetDeviceInfo.CL_DEVICE_VERSION", ReadVolatility.EventDriven("OpenCL driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllOpenClExtension =
        new("gpu_all_opencl_extension", "gpu_adapter_extension", "all", "text", "text",
            "gpu.exact_opencl_device_extension_name_reported_by_CL_DEVICE_EXTENSIONS",
            "opencl.clGetDeviceInfo.CL_DEVICE_EXTENSIONS", ReadVolatility.EventDriven("OpenCL driver capability configuration changed"));
}
