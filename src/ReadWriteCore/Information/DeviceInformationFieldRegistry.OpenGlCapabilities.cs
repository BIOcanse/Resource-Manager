namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllOpenGlVersion =
        new("gpu_all_opengl_version", "gpu_adapter", "all", "text", "text",
            "gpu.opengl_version_string_reported_by_a_current_context_matching_the_exact_dxgi_luid_and_node_mask",
            "opengl.glGetString(GL_VERSION)", ReadVolatility.EventDriven("OpenGL driver capability configuration changed"));

    public static readonly DeviceInformationDefinition GpuAllOpenGlExtension =
        new("gpu_all_opengl_extension", "gpu_adapter_extension", "all", "text", "text",
            "gpu.opengl_extension_name_reported_by_a_current_context_matching_the_exact_dxgi_luid_and_node_mask",
            "opengl.glGetStringi(GL_EXTENSIONS, index); legacy glGetString(GL_EXTENSIONS)", ReadVolatility.EventDriven("OpenGL driver capability configuration changed"));
}
