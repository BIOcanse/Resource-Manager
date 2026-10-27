namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllD3dFeatureLevel =
        new("gpu_all_d3d_feature_level", "gpu_adapter", "all", "unsigned-integer", "enum",
            "gpu.d3d_api_reported_feature_level_code_for_exact_dxgi_adapter", "windows.d3d11-or-d3d12.capability-query", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllD3dFormatSupportFlags =
        new("gpu_all_d3d_format_support_flags", "gpu_adapter_format", "all", "unsigned64", "bitmask64",
            "gpu.d3d_format_support_flags_for_caller_selected_dxgi_format_not_all_formats_or_application_support",
            "windows.d3d11-or-d3d12.capability-query", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));
    public static readonly DeviceInformationDefinition GpuAllD3d12RaytracingTier =
        new("gpu_all_d3d12_raytracing_tier", "gpu_adapter", "all", "unsigned-integer", "enum",
            "gpu.d3d12_reported_raytracing_tier_raw_code_not_enabled_or_performance", "windows.d3d12.capability-query", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));
}
