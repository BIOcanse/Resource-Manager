namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxTextureSize =
        new("gpu_all_opengl_max_texture_size", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.opengl_current_context_max_texture_size_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_TEXTURE_SIZE)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMax3DTextureSize =
        new("gpu_all_opengl_max_3d_texture_size", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.opengl_current_context_max_3d_texture_size_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_3D_TEXTURE_SIZE)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxCubeMapTextureSize =
        new("gpu_all_opengl_max_cube_map_texture_size", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.opengl_current_context_max_cube_map_texture_size_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_CUBE_MAP_TEXTURE_SIZE)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxTextureLodBias =
        new("gpu_all_opengl_max_texture_lod_bias", "gpu_adapter", "all", "number", "lod-bias",
            "gpu.opengl_current_context_max_texture_lod_bias_exact_luid_node_mask", "opengl.glGetFloatv(GL_MAX_TEXTURE_LOD_BIAS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxVertexAttribs =
        new("gpu_all_opengl_max_vertex_attribs", "gpu_adapter", "all", "uint32", "count",
            "gpu.opengl_current_context_max_vertex_attribs_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_VERTEX_ATTRIBS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxTextureImageUnits =
        new("gpu_all_opengl_max_texture_image_units", "gpu_adapter", "all", "uint32", "count",
            "gpu.opengl_current_context_max_texture_image_units_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_TEXTURE_IMAGE_UNITS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxCombinedTextureImageUnits =
        new("gpu_all_opengl_max_combined_texture_image_units", "gpu_adapter", "all", "uint32", "count",
            "gpu.opengl_current_context_max_combined_texture_image_units_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxDrawBuffers =
        new("gpu_all_opengl_max_draw_buffers", "gpu_adapter", "all", "uint32", "count",
            "gpu.opengl_current_context_max_draw_buffers_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_DRAW_BUFFERS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxRenderbufferSize =
        new("gpu_all_opengl_max_renderbuffer_size", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.opengl_current_context_max_renderbuffer_size_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_RENDERBUFFER_SIZE)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxArrayTextureLayers =
        new("gpu_all_opengl_max_array_texture_layers", "gpu_adapter", "all", "uint32", "layers",
            "gpu.opengl_current_context_max_array_texture_layers_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_ARRAY_TEXTURE_LAYERS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxUniformBufferBindings =
        new("gpu_all_opengl_max_uniform_buffer_bindings", "gpu_adapter", "all", "uint32", "count",
            "gpu.opengl_current_context_max_uniform_buffer_bindings_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_UNIFORM_BUFFER_BINDINGS)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxUniformBlockSize =
        new("gpu_all_opengl_max_uniform_block_size", "gpu_adapter", "all", "uint32", "B",
            "gpu.opengl_current_context_max_uniform_block_size_exact_luid_node_mask", "opengl.glGetIntegerv(GL_MAX_UNIFORM_BLOCK_SIZE)");
    public static readonly DeviceInformationDefinition GpuAllOpenGlMaxShaderStorageBlockSize =
        new("gpu_all_opengl_max_shader_storage_block_size", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.opengl_current_context_max_shader_storage_block_size_exact_luid_node_mask", "opengl.glGetInteger64v(GL_MAX_SHADER_STORAGE_BLOCK_SIZE)");
}
