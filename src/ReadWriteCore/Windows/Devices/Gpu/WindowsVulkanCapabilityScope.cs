using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

[Flags]
public enum WindowsVulkanCapabilityFields
{
    None = 0,
    ApiVersion = 1,
    Extensions = 2,
    CoreFeatures = 4,
    MaxImageDimension1D = 8,
    MaxImageDimension2D = 16,
    MaxImageDimension3D = 32,
    MaxImageDimensionCube = 64,
    MaxImageArrayLayers = 128,
    CoreLimits = 256,
    ImageDimensionLimits = MaxImageDimension1D | MaxImageDimension2D | MaxImageDimension3D,
    CubeArrayLimits = MaxImageDimensionCube | MaxImageArrayLayers,
    All = ApiVersion | Extensions | CoreFeatures
        | ImageDimensionLimits
}

/// <summary>Finite Vulkan fields for exact DXGI LUID/PCI adapter identities supplied by the caller.</summary>
public sealed record WindowsVulkanCapabilityScope
{
    private readonly ImmutableArray<DeviceInformationDefinition> definitions;

    internal const WindowsVulkanCapabilityFields KnownFields = WindowsVulkanCapabilityFields.All
        | WindowsVulkanCapabilityFields.CubeArrayLimits
        | WindowsVulkanCapabilityFields.CoreLimits;

    public WindowsVulkanCapabilityFields Fields { get; }
    public ImmutableArray<WindowsD3dAdapterIdentity> Adapters { get; }
    public int MaximumSelectedAdapterCount { get; }
    public int MaximumPhysicalDeviceCount { get; }
    public int MaximumExtensionCount { get; }

    public WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields fields,
        IEnumerable<WindowsD3dAdapterIdentity> adapters, int maximumSelectedAdapterCount = 64,
        int maximumPhysicalDeviceCount = 512, int maximumExtensionCount = 4096)
    {
        if (fields == WindowsVulkanCapabilityFields.None || (fields & ~KnownFields) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        ArgumentNullException.ThrowIfNull(adapters);
        if (maximumSelectedAdapterCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumSelectedAdapterCount));
        if (maximumPhysicalDeviceCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumPhysicalDeviceCount));
        if (maximumExtensionCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumExtensionCount));

        var values = ReadBounded(adapters, maximumSelectedAdapterCount, nameof(adapters));
        if (values.Length == 0 || values.Any(static adapter => adapter.Luid == 0
                || adapter.PciVendorId == 0 || adapter.PciDeviceId == 0)
            || values.Select(static adapter => adapter.Luid).Distinct().Count() != values.Length)
            throw new ArgumentException("Select unique exact DXGI LUID/PCI adapter identities.", nameof(adapters));

        Fields = fields;
        Adapters = [.. values.OrderBy(static adapter => adapter.Luid)];
        MaximumSelectedAdapterCount = maximumSelectedAdapterCount;
        MaximumPhysicalDeviceCount = maximumPhysicalDeviceCount;
        MaximumExtensionCount = maximumExtensionCount;
        definitions = BuildDefinitions(fields);
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions => definitions;

    private static ImmutableArray<DeviceInformationDefinition> BuildDefinitions(
        WindowsVulkanCapabilityFields fields)
    {
        var result = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(109);
        if ((fields & WindowsVulkanCapabilityFields.ApiVersion) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanApiVersion);
        if ((fields & WindowsVulkanCapabilityFields.Extensions) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanExtension);
        if ((fields & WindowsVulkanCapabilityFields.CoreFeatures) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanFeature);
        if ((fields & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D);
        if ((fields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D);
        if ((fields & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D);
        if ((fields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube);
        if ((fields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers);
        if ((fields & WindowsVulkanCapabilityFields.CoreLimits) != 0)
            result.AddRange(WindowsVulkanCoreLimitsCatalog.Definitions);
        return result.ToImmutable();
    }

    internal WindowsVulkanCapabilitySelection FullSelection
        => new(Fields, Definitions, [], true);

    internal WindowsVulkanCapabilitySelection Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var selected = WindowsVulkanCapabilityFields.None;
        var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>();
        var seenDefinitions = new HashSet<DeviceInformationDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var item in selection)
        {
            WindowsVulkanCapabilityFields field;
            if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanApiVersion))
                field = WindowsVulkanCapabilityFields.ApiVersion;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanExtension))
                field = WindowsVulkanCapabilityFields.Extensions;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanFeature))
                field = WindowsVulkanCapabilityFields.CoreFeatures;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D))
                field = WindowsVulkanCapabilityFields.MaxImageDimension1D;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D))
                field = WindowsVulkanCapabilityFields.MaxImageDimension2D;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D))
                field = WindowsVulkanCapabilityFields.MaxImageDimension3D;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube))
                field = WindowsVulkanCapabilityFields.MaxImageDimensionCube;
            else if (ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers))
                field = WindowsVulkanCapabilityFields.MaxImageArrayLayers;
            else if (WindowsVulkanCoreLimitsCatalog.TryGetBinding(item.Definition, out _))
                field = WindowsVulkanCapabilityFields.CoreLimits;
            else throw new ArgumentException("Unknown Vulkan capability definition.", nameof(selection));
            if (item.Field.FieldName != item.Definition.Name
                || (Fields & field) == 0
                || (field != WindowsVulkanCapabilityFields.CoreLimits
                    && !ReferenceEquals(item.Definition, DefinitionFor(field))))
                throw new ArgumentException("Vulkan selection must use an exact static field inside this scope.", nameof(selection));
            selected |= field;
            if (seenDefinitions.Add(item.Definition)) definitions.Add(item.Definition);
        }
        return new(selected, definitions.ToImmutable(), selection, false);
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsVulkanCapabilityFields field) => field switch
    {
        WindowsVulkanCapabilityFields.ApiVersion => DeviceInformationFieldRegistry.GpuAllVulkanApiVersion,
        WindowsVulkanCapabilityFields.Extensions => DeviceInformationFieldRegistry.GpuAllVulkanExtension,
        WindowsVulkanCapabilityFields.CoreFeatures => DeviceInformationFieldRegistry.GpuAllVulkanFeature,
        WindowsVulkanCapabilityFields.MaxImageDimension1D => DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension1D,
        WindowsVulkanCapabilityFields.MaxImageDimension2D => DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension2D,
        WindowsVulkanCapabilityFields.MaxImageDimension3D => DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimension3D,
        WindowsVulkanCapabilityFields.MaxImageDimensionCube => DeviceInformationFieldRegistry.GpuAllVulkanMaxImageDimensionCube,
        WindowsVulkanCapabilityFields.MaxImageArrayLayers => DeviceInformationFieldRegistry.GpuAllVulkanMaxImageArrayLayers,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
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

internal sealed record WindowsVulkanCapabilitySelection(
    WindowsVulkanCapabilityFields QueryFields,
    ImmutableArray<DeviceInformationDefinition> Definitions,
    ImmutableArray<InformationFieldSelection> ExactFields,
    bool WholeScope)
{
    internal bool Includes(FieldReference field, DeviceInformationDefinition definition)
    {
        if (WholeScope) return Definitions.Any(candidate => ReferenceEquals(candidate, definition));
        foreach (var item in ExactFields)
            if (item.Field == field && ReferenceEquals(item.Definition, definition)) return true;
        return false;
    }
}
