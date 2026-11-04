namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllVideoDecoderOutputFormat =
        new("gpu_all_video_decoder_output_format", "gpu_adapter_decoder_format", "all", "unsigned-integer", "dxgi_format",
            "gpu.d3d11_decoder_output_format_supported_for_exact_adapter_and_profile", "windows.d3d11-video-device", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllVideoDecoderProfileGuid =
        new("gpu_all_video_decoder_profile_guid", "gpu_adapter_decoder_profile", "all", "text", "guid",
            "gpu.d3d11_decoder_profile_guid_enumerated_for_exact_adapter", "windows.d3d11-video-device", ReadVolatility.EventDriven("Direct3D driver capability configuration changed"));
}
