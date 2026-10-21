using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

[Flags]
public enum WindowsOpenGlCapabilityFields
{
    None = 0,
    Version = 1,
    Extensions = 2,
    MaxTextureSize = 4,
    Max3DTextureSize = 8,
    MaxCubeMapTextureSize = 16,
    MaxTextureLodBias = 32,
    MaxVertexAttribs = 64,
    MaxTextureImageUnits = 128,
    MaxCombinedTextureImageUnits = 256,
    MaxDrawBuffers = 512,
    MaxRenderbufferSize = 1024,
    MaxArrayTextureLayers = 2048,
    MaxUniformBufferBindings = 4096,
    MaxUniformBlockSize = 8192,
    MaxShaderStorageBlockSize = 16384,
    Limits = MaxTextureSize | Max3DTextureSize | MaxCubeMapTextureSize | MaxTextureLodBias
        | MaxVertexAttribs | MaxTextureImageUnits | MaxCombinedTextureImageUnits | MaxDrawBuffers
        | MaxRenderbufferSize | MaxArrayTextureLayers | MaxUniformBufferBindings | MaxUniformBlockSize
        | MaxShaderStorageBlockSize,
    All = Version | Extensions
}

/// <summary>An exact DXGI adapter identity and the exact OpenGL context device-node mask requested.
/// The Khronos GL context query supplies LUID and node mask; PCI identity remains the caller's paired DXGI evidence.</summary>
public readonly record struct WindowsOpenGlAdapterTarget
{
    public WindowsD3dAdapterIdentity Adapter { get; }
    public uint NodeMask { get; }

    public WindowsOpenGlAdapterTarget(WindowsD3dAdapterIdentity adapter, uint nodeMask)
    {
        if (adapter.Luid == 0 || adapter.PciVendorId == 0 || adapter.PciDeviceId == 0)
            throw new ArgumentOutOfRangeException(nameof(adapter));
        if (nodeMask == 0) throw new ArgumentOutOfRangeException(nameof(nodeMask));
        Adapter = adapter;
        NodeMask = nodeMask;
    }
}

/// <summary>Finite explicit OpenGL capability request. Bounds apply to context count and native GL strings.</summary>
public sealed record WindowsOpenGlCapabilityScope
{
    public WindowsOpenGlCapabilityFields Fields { get; }
    public ImmutableArray<WindowsOpenGlAdapterTarget> Targets { get; }
    public int MaximumTargetCount { get; }
    public int MaximumExtensionCount { get; }
    public int MaximumTextBytes { get; }
    public int MaximumAggregateTextBytes { get; }

    public WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields fields,
        IEnumerable<WindowsOpenGlAdapterTarget> targets, int maximumTargetCount = 64,
        int maximumExtensionCount = 4096, int maximumTextBytes = 65536,
        int maximumAggregateTextBytes = 1_048_576)
    {
        var knownFields = WindowsOpenGlCapabilityFields.All | WindowsOpenGlCapabilityFields.Limits;
        if (fields == WindowsOpenGlCapabilityFields.None || (fields & ~knownFields) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        ArgumentNullException.ThrowIfNull(targets);
        if (maximumTargetCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumTargetCount));
        if (maximumExtensionCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumExtensionCount));
        if (maximumTextBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumTextBytes));
        if (maximumAggregateTextBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumAggregateTextBytes));

        var values = ReadBounded(targets, maximumTargetCount, nameof(targets));
        if (values.Length == 0 || values.Any(static target => target.Adapter.Luid == 0 || target.NodeMask == 0)
            || values.Select(static target => (target.Adapter.Luid, target.NodeMask)).Distinct().Count() != values.Length
            || values.GroupBy(static target => target.Adapter.Luid).Any(static group =>
                group.Select(static target => (target.Adapter.PciVendorId, target.Adapter.PciDeviceId)).Distinct().Count() != 1))
            throw new ArgumentException("Select unique exact DXGI adapter and OpenGL node-mask targets.", nameof(targets));

        Fields = fields;
        Targets = [.. values.OrderBy(static target => target.Adapter.Luid).ThenBy(static target => target.NodeMask)];
        MaximumTargetCount = maximumTargetCount;
        MaximumExtensionCount = maximumExtensionCount;
        MaximumTextBytes = maximumTextBytes;
        MaximumAggregateTextBytes = maximumAggregateTextBytes;
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var result = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(15);
            if ((Fields & WindowsOpenGlCapabilityFields.Version) != 0)
                result.Add(DeviceInformationFieldRegistry.GpuAllOpenGlVersion);
            if ((Fields & WindowsOpenGlCapabilityFields.Extensions) != 0)
                result.Add(DeviceInformationFieldRegistry.GpuAllOpenGlExtension);
            foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
                if ((Fields & spec.Field) != 0) result.Add(spec.Definition);
            return result.ToImmutable();
        }
    }

    internal WindowsOpenGlCapabilityFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var selected = WindowsOpenGlCapabilityFields.None;
        foreach (var item in selection)
        {
            WindowsOpenGlCapabilityFields field;
            if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllOpenGlVersion))
                field = WindowsOpenGlCapabilityFields.Version;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllOpenGlExtension))
                field = WindowsOpenGlCapabilityFields.Extensions;
            else if (WindowsOpenGlCoreLimitsCatalog.TrySpecForField(item.Definition, out var spec))
                field = spec.Field;
            else throw new ArgumentException("Unknown OpenGL capability definition.", nameof(selection));
            if (item.Field.FieldName != item.Definition.Name
                || !ReferenceEquals(item.Definition, DefinitionFor(field)) || (Fields & field) == 0)
                throw new ArgumentException("OpenGL selection must use an exact static field inside this scope.", nameof(selection));
            selected |= field;
        }
        return selected;
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsOpenGlCapabilityFields field)
        => field switch
        {
            WindowsOpenGlCapabilityFields.Version => DeviceInformationFieldRegistry.GpuAllOpenGlVersion,
            WindowsOpenGlCapabilityFields.Extensions => DeviceInformationFieldRegistry.GpuAllOpenGlExtension,
            _ => WindowsOpenGlCoreLimitsCatalog.SpecForField(field).Definition
        };

    private static ImmutableArray<T> ReadBounded<T>(IEnumerable<T> source, int maximumCount, string parameterName)
    {
        var values = ImmutableArray.CreateBuilder<T>();
        using var enumerator = source.GetEnumerator();
        while (values.Count < maximumCount && enumerator.MoveNext()) values.Add(enumerator.Current);
        if (values.Count == maximumCount && enumerator.MoveNext())
            throw new ArgumentException("Input exceeds its configured finite bound.", parameterName);
        return values.ToImmutable();
    }
}
