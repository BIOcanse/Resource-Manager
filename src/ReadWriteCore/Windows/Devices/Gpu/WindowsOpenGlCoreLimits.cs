using System.Collections.Immutable;
using System.Numerics;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Fixed OpenGL implementation-limit tokens supported by this reader. No arbitrary GLenum is accepted.</summary>
public enum WindowsOpenGlCoreLimit
{
    MaxTextureSize,
    Max3DTextureSize,
    MaxCubeMapTextureSize,
    MaxTextureLodBias,
    MaxVertexAttribs,
    MaxTextureImageUnits,
    MaxCombinedTextureImageUnits,
    MaxDrawBuffers,
    MaxRenderbufferSize,
    MaxArrayTextureLayers,
    MaxUniformBufferBindings,
    MaxUniformBlockSize,
    MaxShaderStorageBlockSize
}

/// <summary>Reason a selected exact-context limit did not produce a value.</summary>
public enum WindowsOpenGlLimitReadStatus
{
    Queried,
    Unsupported,
    Unavailable
}

/// <summary>A typed limit result. Exactly one value member is populated for Queried; none for other statuses.</summary>
public sealed record WindowsOpenGlLimitRead(
    WindowsOpenGlLimitReadStatus Status,
    uint? Unsigned32Value = null,
    ulong? Unsigned64Value = null,
    double? NumberValue = null);

internal enum WindowsOpenGlLimitValueKind { Unsigned32, Unsigned64, Number }

internal sealed record WindowsOpenGlCoreLimitSpec(
    WindowsOpenGlCoreLimit Limit,
    WindowsOpenGlCapabilityFields Field,
    DeviceInformationDefinition Definition,
    uint Token,
    WindowsOpenGlLimitValueKind ValueKind,
    int CoreMajor,
    int CoreMinor,
    string? Extension);

internal static class WindowsOpenGlCoreLimitsCatalog
{
    private static readonly ImmutableArray<WindowsOpenGlCoreLimitSpec> specs =
    [
        new(WindowsOpenGlCoreLimit.MaxTextureSize, WindowsOpenGlCapabilityFields.MaxTextureSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxTextureSize, 0x0D33, WindowsOpenGlLimitValueKind.Unsigned32, 1, 0, null),
        new(WindowsOpenGlCoreLimit.Max3DTextureSize, WindowsOpenGlCapabilityFields.Max3DTextureSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMax3DTextureSize, 0x8073, WindowsOpenGlLimitValueKind.Unsigned32, 1, 2, "GL_EXT_texture3D"),
        new(WindowsOpenGlCoreLimit.MaxCubeMapTextureSize, WindowsOpenGlCapabilityFields.MaxCubeMapTextureSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxCubeMapTextureSize, 0x851C, WindowsOpenGlLimitValueKind.Unsigned32, 1, 3, "GL_ARB_texture_cube_map"),
        new(WindowsOpenGlCoreLimit.MaxTextureLodBias, WindowsOpenGlCapabilityFields.MaxTextureLodBias,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxTextureLodBias, 0x84FD, WindowsOpenGlLimitValueKind.Number, 1, 4, "GL_EXT_texture_lod_bias"),
        new(WindowsOpenGlCoreLimit.MaxVertexAttribs, WindowsOpenGlCapabilityFields.MaxVertexAttribs,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxVertexAttribs, 0x8869, WindowsOpenGlLimitValueKind.Unsigned32, 2, 0, "GL_ARB_vertex_shader"),
        new(WindowsOpenGlCoreLimit.MaxTextureImageUnits, WindowsOpenGlCapabilityFields.MaxTextureImageUnits,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxTextureImageUnits, 0x8872, WindowsOpenGlLimitValueKind.Unsigned32, 2, 0, "GL_ARB_fragment_shader"),
        new(WindowsOpenGlCoreLimit.MaxCombinedTextureImageUnits, WindowsOpenGlCapabilityFields.MaxCombinedTextureImageUnits,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxCombinedTextureImageUnits, 0x8B4D, WindowsOpenGlLimitValueKind.Unsigned32, 2, 0, "GL_ARB_vertex_shader"),
        new(WindowsOpenGlCoreLimit.MaxDrawBuffers, WindowsOpenGlCapabilityFields.MaxDrawBuffers,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxDrawBuffers, 0x8824, WindowsOpenGlLimitValueKind.Unsigned32, 2, 0, "GL_ARB_draw_buffers"),
        new(WindowsOpenGlCoreLimit.MaxRenderbufferSize, WindowsOpenGlCapabilityFields.MaxRenderbufferSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxRenderbufferSize, 0x84E8, WindowsOpenGlLimitValueKind.Unsigned32, 3, 0, "GL_ARB_framebuffer_object"),
        new(WindowsOpenGlCoreLimit.MaxArrayTextureLayers, WindowsOpenGlCapabilityFields.MaxArrayTextureLayers,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxArrayTextureLayers, 0x88FF, WindowsOpenGlLimitValueKind.Unsigned32, 3, 0, "GL_EXT_texture_array"),
        new(WindowsOpenGlCoreLimit.MaxUniformBufferBindings, WindowsOpenGlCapabilityFields.MaxUniformBufferBindings,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxUniformBufferBindings, 0x8A2F, WindowsOpenGlLimitValueKind.Unsigned32, 3, 1, "GL_ARB_uniform_buffer_object"),
        new(WindowsOpenGlCoreLimit.MaxUniformBlockSize, WindowsOpenGlCapabilityFields.MaxUniformBlockSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxUniformBlockSize, 0x8A30, WindowsOpenGlLimitValueKind.Unsigned32, 3, 1, "GL_ARB_uniform_buffer_object"),
        new(WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize, WindowsOpenGlCapabilityFields.MaxShaderStorageBlockSize,
            DeviceInformationFieldRegistry.GpuAllOpenGlMaxShaderStorageBlockSize, 0x90DE, WindowsOpenGlLimitValueKind.Unsigned64, 4, 3, "GL_ARB_shader_storage_buffer_object")
    ];

    internal static ImmutableArray<WindowsOpenGlCoreLimitSpec> Specs => specs;

    internal static WindowsOpenGlCoreLimitSpec Spec(WindowsOpenGlCoreLimit limit)
    {
        var index = (int)limit;
        if ((uint)index >= (uint)specs.Length || specs[index].Limit != limit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return specs[index];
    }

    internal static WindowsOpenGlCoreLimitSpec SpecForField(WindowsOpenGlCapabilityFields field)
    {
        var rawField = (uint)field;
        if (rawField < (uint)WindowsOpenGlCapabilityFields.MaxTextureSize
            || (rawField & (rawField - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(field));
        var index = BitOperations.TrailingZeroCount(rawField) - 2;
        if ((uint)index >= (uint)specs.Length || specs[index].Field != field)
            throw new ArgumentOutOfRangeException(nameof(field));
        return specs[index];
    }

    internal static bool TrySpecForField(DeviceInformationDefinition definition, out WindowsOpenGlCoreLimitSpec result)
    {
        foreach (var spec in specs)
            if (ReferenceEquals(spec.Definition, definition))
            {
                result = spec;
                return true;
            }
        result = null!;
        return false;
    }

    internal static bool IsSupported(WindowsOpenGlCoreLimit limit, int major, int minor,
        IReadOnlyCollection<string> extensions)
    {
        var spec = Spec(limit);
        return major > spec.CoreMajor || major == spec.CoreMajor && minor >= spec.CoreMinor
            || spec.Extension is not null && extensions.Contains(spec.Extension, StringComparer.Ordinal);
    }

    internal static bool IsValidRead(WindowsOpenGlCoreLimit limit, WindowsOpenGlLimitRead? value)
    {
        if (value is null) return false;
        var populated = (value.Unsigned32Value.HasValue ? 1 : 0)
            + (value.Unsigned64Value.HasValue ? 1 : 0) + (value.NumberValue.HasValue ? 1 : 0);
        if (value.Status != WindowsOpenGlLimitReadStatus.Queried)
            return populated == 0 && (value.Status is WindowsOpenGlLimitReadStatus.Unsupported
                or WindowsOpenGlLimitReadStatus.Unavailable);
        var spec = Spec(limit);
        return spec.ValueKind switch
        {
            WindowsOpenGlLimitValueKind.Unsigned32 => value.Unsigned32Value.HasValue && populated == 1,
            WindowsOpenGlLimitValueKind.Unsigned64 => value.Unsigned64Value.HasValue && populated == 1,
            WindowsOpenGlLimitValueKind.Number => value.NumberValue is { } number
                && double.IsFinite(number) && number >= 0 && populated == 1,
            _ => false
        };
    }
}
