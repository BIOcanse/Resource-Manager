using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

[Flags]
public enum WindowsOpenClCapabilityFields
{
    None = 0,
    DeviceVersion = 1,
    Extensions = 2,
    All = DeviceVersion | Extensions
}

/// <summary>An exact Windows DXGI LUID and the single OpenCL node requested from that adapter.</summary>
public readonly record struct WindowsOpenClAdapterTarget
{
    public ulong DxgiLuid { get; }
    public uint NodeMask { get; }

    public WindowsOpenClAdapterTarget(ulong dxgiLuid, uint nodeMask)
    {
        if (nodeMask == 0 || (nodeMask & (nodeMask - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nodeMask), "OpenCL device node masks must contain exactly one bit.");
        DxgiLuid = dxgiLuid;
        NodeMask = nodeMask;
    }
}

/// <summary>Finite caller-selected OpenCL fields and exact DXGI LUID/node targets.
/// Bounds limit enumeration and text sizes; callers may raise them when their inventory is larger.</summary>
public sealed record WindowsOpenClCapabilityScope
{
    public WindowsOpenClCapabilityFields Fields { get; }
    public ImmutableArray<WindowsOpenClAdapterTarget> Targets { get; }
    public int MaximumTargetCount { get; }
    public int MaximumPlatformCount { get; }
    public int MaximumDeviceCount { get; }
    public int MaximumExtensionCount { get; }
    public int MaximumTextBytes { get; }

    public WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields fields,
        IEnumerable<WindowsOpenClAdapterTarget> targets,
        int maximumTargetCount = 64, int maximumPlatformCount = 64,
        int maximumDeviceCount = 1024, int maximumExtensionCount = 4096,
        int maximumTextBytes = 65536)
    {
        if (fields == WindowsOpenClCapabilityFields.None || (fields & ~WindowsOpenClCapabilityFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        ArgumentNullException.ThrowIfNull(targets);
        if (maximumTargetCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumTargetCount));
        if (maximumPlatformCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumPlatformCount));
        if (maximumDeviceCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumDeviceCount));
        if (maximumExtensionCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumExtensionCount));
        if (maximumTextBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumTextBytes));

        var targetArray = ReadBounded(targets, maximumTargetCount, nameof(targets));
        if (targetArray.Length == 0 || targetArray.Any(static target => target.NodeMask == 0
                || (target.NodeMask & (target.NodeMask - 1)) != 0)
            || targetArray.Distinct().Count() != targetArray.Length)
            throw new ArgumentException("Select unique exact DXGI LUID and single-bit OpenCL node targets.", nameof(targets));

        Fields = fields;
        Targets = [.. targetArray.OrderBy(static target => target.DxgiLuid).ThenBy(static target => target.NodeMask)];
        MaximumTargetCount = maximumTargetCount;
        MaximumPlatformCount = maximumPlatformCount;
        MaximumDeviceCount = maximumDeviceCount;
        MaximumExtensionCount = maximumExtensionCount;
        MaximumTextBytes = maximumTextBytes;
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(2);
            if ((Fields & WindowsOpenClCapabilityFields.DeviceVersion) != 0)
                definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion);
            if ((Fields & WindowsOpenClCapabilityFields.Extensions) != 0)
                definitions.Add(DeviceInformationFieldRegistry.GpuAllOpenClExtension);
            return definitions.ToImmutable();
        }
    }

    internal WindowsOpenClCapabilityFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var selected = WindowsOpenClCapabilityFields.None;
        foreach (var item in selection)
        {
            var field = ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion)
                ? WindowsOpenClCapabilityFields.DeviceVersion
                : ReferenceEquals(item.Definition, DeviceInformationFieldRegistry.GpuAllOpenClExtension)
                    ? WindowsOpenClCapabilityFields.Extensions
                    : throw new ArgumentException("Unknown OpenCL capability definition.", nameof(selection));
            if (item.Field.FieldName != item.Definition.Name
                || !ReferenceEquals(item.Definition, DefinitionFor(field))
                || (Fields & field) == 0)
                throw new ArgumentException("OpenCL selection must use an exact static field inside this scope.", nameof(selection));
            selected |= field;
        }
        return selected;
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsOpenClCapabilityFields field) => field switch
    {
        WindowsOpenClCapabilityFields.DeviceVersion => DeviceInformationFieldRegistry.GpuAllOpenClDeviceVersion,
        WindowsOpenClCapabilityFields.Extensions => DeviceInformationFieldRegistry.GpuAllOpenClExtension,
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
