namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllVideoDecoderConfiguration =
        new("gpu_all_video_decoder_configuration", "gpu_adapter_decoder_configuration", "all", "object",
            "d3d11_video_decoder_configuration_set",
            "gpu.d3d11_decoder_configurations_for_caller_supplied_exact_profile_dimensions_and_output_format",
            "windows.d3d11-video-device", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllVideoProcessorCaps =
        new("gpu_all_video_processor_caps", "gpu_adapter_video_processor", "all", "object",
            "d3d11_video_processor_caps",
            "gpu.d3d11_video_processor_caps_for_caller_supplied_exact_content_descriptor",
            "windows.d3d11-video-processor-enumerator", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllVideoProcessorRateConversionCaps =
        new("gpu_all_video_processor_rate_conversion_caps", "gpu_adapter_video_processor", "all", "object",
            "d3d11_video_processor_rate_conversion_caps",
            "gpu.d3d11_video_processor_rate_conversion_groups_for_caller_supplied_exact_content_descriptor",
            "windows.d3d11-video-processor-enumerator", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));
}
