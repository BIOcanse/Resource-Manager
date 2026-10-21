namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition AudioAllDefaultRoleActual =
        new("audio_all_default_role_actual", "audio_endpoint", "all", "text-list", "enum",
            "audio.endpoint.default_roles_actual:console|multimedia|communications", "windows.audio-endpoint.mmdevice");

    public static readonly DeviceInformationDefinition AudioAllDriverVersionDeclared =
        new("audio_all_driver_version_declared", "audio_device", "all", "text", "text",
            "audio.device.connected_adapter_driver_version_declared", "windows.audio-endpoint.connector-cfgmgr32");

    public static readonly DeviceInformationDefinition AudioAllEndpointIdDeclared =
        new("audio_all_endpoint_id_declared", "audio_endpoint", "all", "text", "text",
            "audio.endpoint.windows_immdevice_id_declared", "windows.audio-endpoint.mmdevice");

    public static readonly DeviceInformationDefinition AudioAllMixFormatConfigured =
        new("audio_all_mix_format_configured", "audio_endpoint", "all", "object", "format",
            "audio.endpoint.shared_engine_mix_format_configured_not_all_supported_formats", "windows.audio-endpoint.iaudioclient-mix-format");

    public static readonly DeviceInformationDefinition CodecAllMediaTypeCapability =
        new("codec_all_media_type_capability", "codec", "all", "object", "enum",
            "codec.registered_mft_types_or_passive_directshow_declaration_not_activation_or_hardware_support", "windows.codec-registration.mft-or-directshow");

    public static readonly DeviceInformationDefinition CodecAllVersionDeclared =
        new("codec_all_version_declared", "codec", "all", "text", "text",
            "codec.exact_clsid_inprocserver32_file_version_resource_in_explicit_registry_view", "windows.codec-registration.inprocserver32-file-version");
}
