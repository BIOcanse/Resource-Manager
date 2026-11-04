using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

[Flags]
public enum WindowsD3dCapabilityFields
{
    None = 0,
    FeatureLevel = 1,
    FormatSupportFlags = 2,
    DecoderProfiles = 4,
    DecoderOutputFormats = 8,
    DecoderConfigurations = 16,
    VideoProcessorCaps = 32,
    VideoProcessorRateConversionCaps = 64,
    RaytracingTier = 128,
    /// <summary>Original feature-level and DXGI-format-support fields, retained for source compatibility.</summary>
    All = FeatureLevel | FormatSupportFlags
}

public enum WindowsD3dApiVersion
{
    Direct3D11 = 11,
    Direct3D12 = 12
}

/// <summary>The exact D3D11_VIDEO_DECODER_DESC supplied to the configuration query.</summary>
public readonly record struct WindowsD3dVideoDecoderDescriptor(
    Guid ProfileGuid, uint SampleWidth, uint SampleHeight, WindowsD3dFormat OutputFormat);

public enum WindowsD3dVideoFrameFormat : uint
{
    Progressive = 0,
    InterlacedTopFieldFirst = 1,
    InterlacedBottomFieldFirst = 2
}

public enum WindowsD3dVideoUsage : uint
{
    PlaybackNormal = 0,
    OptimalSpeed = 1,
    OptimalQuality = 2
}

/// <summary>The exact unsigned DXGI_RATIONAL members supplied in the processor content descriptor. Zero values are preserved as defined by DXGI_RATIONAL.</summary>
public readonly record struct WindowsD3dRational
{
    public uint Numerator { get; }
    public uint Denominator { get; }

    public WindowsD3dRational(uint numerator, uint denominator)
    {
        if (denominator == 0 && numerator != 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        Numerator = numerator;
        Denominator = denominator;
    }
}

/// <summary>The caller's complete D3D11_VIDEO_PROCESSOR_CONTENT_DESC. Rational pairs are preserved as supplied.</summary>
public readonly record struct WindowsD3dVideoProcessorDescriptor(
    WindowsD3dVideoFrameFormat InputFrameFormat,
    WindowsD3dRational InputFrameRate,
    uint InputWidth,
    uint InputHeight,
    WindowsD3dRational OutputFrameRate,
    uint OutputWidth,
    uint OutputHeight,
    WindowsD3dVideoUsage Usage);

/// <summary>DXGI LUID and PCI IDs observed together in one adapter descriptor.
/// Callers must supply that exact relationship; names and adapter ordering are not identity.</summary>
public readonly record struct WindowsD3dAdapterIdentity
{
    public ulong Luid { get; }
    public ushort PciVendorId { get; }
    public ushort PciDeviceId { get; }

    public WindowsD3dAdapterIdentity(ulong luid, ushort pciVendorId, ushort pciDeviceId)
    {
        if (luid == 0) throw new ArgumentOutOfRangeException(nameof(luid));
        if (pciVendorId == 0) throw new ArgumentOutOfRangeException(nameof(pciVendorId));
        if (pciDeviceId == 0) throw new ArgumentOutOfRangeException(nameof(pciDeviceId));
        Luid = luid;
        PciVendorId = pciVendorId;
        PciDeviceId = pciDeviceId;
    }
}

/// <summary>Static D3D feature-level code values from d3dcommon.h.</summary>
public enum WindowsD3dFeatureLevel : uint
{
    Level9_1 = 0x9100,
    Level9_2 = 0x9200,
    Level9_3 = 0x9300,
    Level10_0 = 0xA000,
    Level10_1 = 0xA100,
    Level11_0 = 0xB000,
    Level11_1 = 0xB100,
    Level12_0 = 0xC000,
    Level12_1 = 0xC100,
    Level12_2 = 0xC200
}

/// <summary>DXGI_FORMAT values present in the reviewed Windows SDK 10.0.26100.0 dxgiformat.h.
/// This finite list deliberately excludes UNKNOWN and FORCE_UINT sentinel values.</summary>
public enum WindowsD3dFormat : uint
{
    R32G32B32A32Typeless = 1,
    R32G32B32A32Float = 2,
    R32G32B32A32UInt = 3,
    R32G32B32A32SInt = 4,
    R32G32B32Typeless = 5,
    R32G32B32Float = 6,
    R32G32B32UInt = 7,
    R32G32B32SInt = 8,
    R16G16B16A16Typeless = 9,
    R16G16B16A16Float = 10,
    R16G16B16A16UNorm = 11,
    R16G16B16A16UInt = 12,
    R16G16B16A16SNorm = 13,
    R16G16B16A16SInt = 14,
    R32G32Typeless = 15,
    R32G32Float = 16,
    R32G32UInt = 17,
    R32G32SInt = 18,
    R32G8X24Typeless = 19,
    D32FloatS8X24UInt = 20,
    R32FloatX8X24Typeless = 21,
    X32TypelessG8X24UInt = 22,
    R10G10B10A2Typeless = 23,
    R10G10B10A2UNorm = 24,
    R10G10B10A2UInt = 25,
    R11G11B10Float = 26,
    R8G8B8A8Typeless = 27,
    R8G8B8A8UNorm = 28,
    R8G8B8A8UNormSrgb = 29,
    R8G8B8A8UInt = 30,
    R8G8B8A8SNorm = 31,
    R8G8B8A8SInt = 32,
    R16G16Typeless = 33,
    R16G16Float = 34,
    R16G16UNorm = 35,
    R16G16UInt = 36,
    R16G16SNorm = 37,
    R16G16SInt = 38,
    R32Typeless = 39,
    D32Float = 40,
    R32Float = 41,
    R32UInt = 42,
    R32SInt = 43,
    R24G8Typeless = 44,
    D24UNormS8UInt = 45,
    R24UNormX8Typeless = 46,
    X24TypelessG8UInt = 47,
    R8G8Typeless = 48,
    R8G8UNorm = 49,
    R8G8UInt = 50,
    R8G8SNorm = 51,
    R8G8SInt = 52,
    R16Typeless = 53,
    R16Float = 54,
    D16UNorm = 55,
    R16UNorm = 56,
    R16UInt = 57,
    R16SNorm = 58,
    R16SInt = 59,
    R8Typeless = 60,
    R8UNorm = 61,
    R8UInt = 62,
    R8SNorm = 63,
    R8SInt = 64,
    A8UNorm = 65,
    R1UNorm = 66,
    R9G9B9E5SharedExp = 67,
    R8G8B8G8UNorm = 68,
    G8R8G8B8UNorm = 69,
    BC1Typeless = 70,
    BC1UNorm = 71,
    BC1UNormSrgb = 72,
    BC2Typeless = 73,
    BC2UNorm = 74,
    BC2UNormSrgb = 75,
    BC3Typeless = 76,
    BC3UNorm = 77,
    BC3UNormSrgb = 78,
    BC4Typeless = 79,
    BC4UNorm = 80,
    BC4SNorm = 81,
    BC5Typeless = 82,
    BC5UNorm = 83,
    BC5SNorm = 84,
    B5G6R5UNorm = 85,
    B5G5R5A1UNorm = 86,
    B8G8R8A8UNorm = 87,
    B8G8R8X8UNorm = 88,
    R10G10B10XrBiasA2UNorm = 89,
    B8G8R8A8Typeless = 90,
    B8G8R8A8UNormSrgb = 91,
    B8G8R8X8Typeless = 92,
    B8G8R8X8UNormSrgb = 93,
    BC6HTypeless = 94,
    BC6HUf16 = 95,
    BC6HSf16 = 96,
    BC7Typeless = 97,
    BC7UNorm = 98,
    BC7UNormSrgb = 99,
    AYuv = 100,
    Y410 = 101,
    Y416 = 102,
    Nv12 = 103,
    P010 = 104,
    P016 = 105,
    Opaque420 = 106,
    Yuy2 = 107,
    Y210 = 108,
    Y216 = 109,
    Nv11 = 110,
    Ai44 = 111,
    Ia44 = 112,
    P8 = 113,
    A8P8 = 114,
    B4G4R4A4UNorm = 115,
    P208 = 130,
    V208 = 131,
    V408 = 132,
    SamplerFeedbackMinMipOpaque = 189,
    SamplerFeedbackMipRegionUsedOpaque = 190,
    A4B4G4R4UNorm = 191
}

/// <summary>An explicit finite Direct3D capability request. Formats apply only when format-support or decoder-output-format fields are selected; processor content descriptors apply only to processor capability fields. Positive enumeration and query bounds are caller-configurable; defaults remain visible in the constructor.</summary>
public sealed record WindowsD3dCapabilityScope
{
    private readonly ImmutableArray<DeviceInformationDefinition> definitions;

    public WindowsD3dCapabilityFields Fields { get; }
    public WindowsD3dApiVersion ApiVersion { get; }
    public ImmutableArray<WindowsD3dAdapterIdentity> Adapters { get; }
    public ImmutableArray<WindowsD3dFormat> Formats { get; }
    public ImmutableArray<WindowsD3dVideoDecoderDescriptor> DecoderConfigurationDescriptors { get; }
    public ImmutableArray<WindowsD3dVideoProcessorDescriptor> VideoProcessorDescriptors { get; }
    /// <summary>Maximum adapter identities read from the caller's finite input (default 64).</summary>
    public int MaximumSelectedAdapterCount { get; }
    /// <summary>Maximum format values read from the caller's finite input (default 64).</summary>
    public int MaximumSelectedFormatCount { get; }
    /// <summary>Maximum DXGI adapter indexes enumerated before an incomplete result is unavailable (default 512).</summary>
    public int MaximumDxgiAdapterCount { get; }
    /// <summary>Maximum D3D11 decoder profiles enumerated per exact adapter (default 256).</summary>
    public int MaximumVideoDecoderProfileCount { get; }
    /// <summary>Maximum profile/format support calls in one adapter query (default 16384).</summary>
    public int MaximumVideoDecoderFormatQueryCount { get; }
    /// <summary>Maximum explicit video decoder descriptions in this request (default 64).</summary>
    public int MaximumVideoDecoderConfigurationDescriptorCount { get; }
    /// <summary>Maximum configurations returned for any one explicit description (default 256).</summary>
    public int MaximumVideoDecoderConfigurationCount { get; }
    /// <summary>Maximum total GetVideoDecoderConfigCount/GetVideoDecoderConfig calls over this read (default 16384).</summary>
    public int MaximumVideoDecoderConfigurationQueryCount { get; }
    /// <summary>Maximum explicit D3D11 processor content descriptors in this request (default 64).</summary>
    public int MaximumVideoProcessorDescriptorCount { get; }
    /// <summary>Maximum total GetVideoProcessorRateConversionCaps calls over this read (default 16384).</summary>
    public int MaximumVideoProcessorRateConversionCapsQueryCount { get; }

    public WindowsD3dCapabilityScope(WindowsD3dCapabilityFields fields, WindowsD3dApiVersion apiVersion,
        IEnumerable<WindowsD3dAdapterIdentity> adapters, IEnumerable<WindowsD3dFormat>? formats = null,
        int maximumSelectedAdapterCount = 64, int maximumSelectedFormatCount = 64,
        int maximumDxgiAdapterCount = 512, int maximumVideoDecoderProfileCount = 256,
        int maximumVideoDecoderFormatQueryCount = 16_384,
        IEnumerable<WindowsD3dVideoDecoderDescriptor>? decoderConfigurationDescriptors = null,
        int maximumVideoDecoderConfigurationDescriptorCount = 64,
        int maximumVideoDecoderConfigurationCount = 256,
        int maximumVideoDecoderConfigurationQueryCount = 16_384,
        IEnumerable<WindowsD3dVideoProcessorDescriptor>? videoProcessorDescriptors = null,
        int maximumVideoProcessorDescriptorCount = 64,
        int maximumVideoProcessorRateConversionCapsQueryCount = 16_384)
    {
        const WindowsD3dCapabilityFields allSupported = WindowsD3dCapabilityFields.FeatureLevel
            | WindowsD3dCapabilityFields.FormatSupportFlags | WindowsD3dCapabilityFields.DecoderProfiles
            | WindowsD3dCapabilityFields.DecoderOutputFormats | WindowsD3dCapabilityFields.DecoderConfigurations
            | WindowsD3dCapabilityFields.VideoProcessorCaps | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps
            | WindowsD3dCapabilityFields.RaytracingTier;
        if (fields == WindowsD3dCapabilityFields.None || (fields & ~allSupported) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (!Enum.IsDefined(apiVersion)) throw new ArgumentOutOfRangeException(nameof(apiVersion));
        if ((fields & (WindowsD3dCapabilityFields.DecoderProfiles | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0
            && apiVersion != WindowsD3dApiVersion.Direct3D11)
            throw new ArgumentException("Video decoder profile and format queries require the D3D11 video-device interface.", nameof(apiVersion));
        if ((fields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0
            && apiVersion != WindowsD3dApiVersion.Direct3D11)
            throw new ArgumentException("Video decoder configuration queries require the D3D11 video-device interface.", nameof(apiVersion));
        if ((fields & (WindowsD3dCapabilityFields.VideoProcessorCaps
            | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps)) != 0
            && apiVersion != WindowsD3dApiVersion.Direct3D11)
            throw new ArgumentException("Video processor capability queries require the D3D11 video-device interface.", nameof(apiVersion));
        if ((fields & WindowsD3dCapabilityFields.RaytracingTier) != 0
            && apiVersion != WindowsD3dApiVersion.Direct3D12)
            throw new ArgumentException("Raytracing tier queries require the D3D12 device interface.", nameof(apiVersion));
        ArgumentNullException.ThrowIfNull(adapters);
        if (maximumSelectedAdapterCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumSelectedAdapterCount));
        if (maximumSelectedFormatCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumSelectedFormatCount));
        if (maximumDxgiAdapterCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumDxgiAdapterCount));
        if (maximumVideoDecoderProfileCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoDecoderProfileCount));
        if (maximumVideoDecoderFormatQueryCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoDecoderFormatQueryCount));
        if (maximumVideoDecoderConfigurationDescriptorCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoDecoderConfigurationDescriptorCount));
        if (maximumVideoDecoderConfigurationCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoDecoderConfigurationCount));
        if (maximumVideoDecoderConfigurationQueryCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoDecoderConfigurationQueryCount));
        if (maximumVideoProcessorDescriptorCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoProcessorDescriptorCount));
        if (maximumVideoProcessorRateConversionCapsQueryCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumVideoProcessorRateConversionCapsQueryCount));
        var adapterArray = ReadBounded(adapters, maximumSelectedAdapterCount, nameof(adapters));
        if (adapterArray.Length == 0
            || adapterArray.Any(static item => item.Luid == 0 || item.PciVendorId == 0 || item.PciDeviceId == 0)
            || adapterArray.Select(static item => item.Luid).Distinct().Count() != adapterArray.Length)
            throw new ArgumentException("Select unique exact DXGI adapter LUID/PCI identities within the configured bound.", nameof(adapters));
        var formatArray = ReadBounded(formats ?? [], maximumSelectedFormatCount, nameof(formats));
        var descriptorArray = ReadBounded(decoderConfigurationDescriptors ?? [],
            maximumVideoDecoderConfigurationDescriptorCount, nameof(decoderConfigurationDescriptors));
        var processorDescriptorArray = ReadBounded(videoProcessorDescriptors ?? [],
            maximumVideoProcessorDescriptorCount, nameof(videoProcessorDescriptors));
        var hasSelectedFormats = (fields & (WindowsD3dCapabilityFields.FormatSupportFlags
            | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0;
        if (hasSelectedFormats)
        {
            if (formatArray.Length == 0 || formatArray.Any(static format => !Enum.IsDefined(format))
                || formatArray.Distinct().Count() != formatArray.Length)
                throw new ArgumentException("Select unique DXGI format candidates from the reviewed SDK list within the configured bound.", nameof(formats));
        }
        else if (formatArray.Length != 0)
        {
            throw new ArgumentException("Formats are valid only when format support is selected.", nameof(formats));
        }
        var configurationsSelected = (fields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0;
        if (configurationsSelected)
        {
            if (descriptorArray.Length == 0
                || descriptorArray.Any(static descriptor => descriptor.ProfileGuid == Guid.Empty
                    || descriptor.SampleWidth == 0 || descriptor.SampleHeight == 0
                    || !Enum.IsDefined(descriptor.OutputFormat))
                || descriptorArray.Distinct().Count() != descriptorArray.Length)
                throw new ArgumentException("Supply unique complete D3D11 decoder descriptions with a nonempty profile, positive dimensions, and a reviewed DXGI format.", nameof(decoderConfigurationDescriptors));
        }
        else if (descriptorArray.Length != 0)
        {
            throw new ArgumentException("Decoder configuration descriptors are valid only when decoder configurations are selected.", nameof(decoderConfigurationDescriptors));
        }

        var processorSelected = (fields & (WindowsD3dCapabilityFields.VideoProcessorCaps
            | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps)) != 0;
        if (processorSelected)
        {
            if (processorDescriptorArray.Length == 0
                || processorDescriptorArray.Any(static descriptor =>
                    !Enum.IsDefined(descriptor.InputFrameFormat) || !Enum.IsDefined(descriptor.Usage)
                    || descriptor.InputWidth == 0 || descriptor.InputHeight == 0
                    || descriptor.OutputWidth == 0 || descriptor.OutputHeight == 0
                    || !IsValidRate(descriptor.InputFrameRate) || !IsValidRate(descriptor.OutputFrameRate))
                || processorDescriptorArray.Distinct().Count() != processorDescriptorArray.Length)
                throw new ArgumentException("Supply unique complete D3D11 processor content descriptors with valid frame enums, DXGI rational pairs, and positive dimensions.", nameof(videoProcessorDescriptors));
        }
        else if (processorDescriptorArray.Length != 0)
        {
            throw new ArgumentException("Processor content descriptors are valid only when processor capabilities are selected.", nameof(videoProcessorDescriptors));
        }

        Fields = fields;
        ApiVersion = apiVersion;
        Adapters = [.. adapterArray.OrderBy(static item => item.Luid)];
        Formats = [.. formatArray.Order()];
        DecoderConfigurationDescriptors = [.. descriptorArray.OrderBy(static descriptor => descriptor.ProfileGuid)
            .ThenBy(static descriptor => descriptor.SampleWidth).ThenBy(static descriptor => descriptor.SampleHeight)
            .ThenBy(static descriptor => (uint)descriptor.OutputFormat)];
        VideoProcessorDescriptors = [.. processorDescriptorArray.OrderBy(static descriptor => (uint)descriptor.InputFrameFormat)
            .ThenBy(static descriptor => descriptor.InputFrameRate.Numerator).ThenBy(static descriptor => descriptor.InputFrameRate.Denominator)
            .ThenBy(static descriptor => descriptor.InputWidth).ThenBy(static descriptor => descriptor.InputHeight)
            .ThenBy(static descriptor => descriptor.OutputFrameRate.Numerator).ThenBy(static descriptor => descriptor.OutputFrameRate.Denominator)
            .ThenBy(static descriptor => descriptor.OutputWidth).ThenBy(static descriptor => descriptor.OutputHeight)
            .ThenBy(static descriptor => (uint)descriptor.Usage)];
        MaximumSelectedAdapterCount = maximumSelectedAdapterCount;
        MaximumSelectedFormatCount = maximumSelectedFormatCount;
        MaximumDxgiAdapterCount = maximumDxgiAdapterCount;
        MaximumVideoDecoderProfileCount = maximumVideoDecoderProfileCount;
        MaximumVideoDecoderFormatQueryCount = maximumVideoDecoderFormatQueryCount;
        MaximumVideoDecoderConfigurationDescriptorCount = maximumVideoDecoderConfigurationDescriptorCount;
        MaximumVideoDecoderConfigurationCount = maximumVideoDecoderConfigurationCount;
        MaximumVideoDecoderConfigurationQueryCount = maximumVideoDecoderConfigurationQueryCount;
        MaximumVideoProcessorDescriptorCount = maximumVideoProcessorDescriptorCount;
        MaximumVideoProcessorRateConversionCapsQueryCount = maximumVideoProcessorRateConversionCapsQueryCount;
        definitions = BuildDefinitions(fields);
    }

    private static ImmutableArray<T> ReadBounded<T>(IEnumerable<T> source, int maximumCount, string parameterName)
    {
        var values = ImmutableArray.CreateBuilder<T>();
        using var enumerator = source.GetEnumerator();
        while (values.Count < maximumCount && enumerator.MoveNext()) values.Add(enumerator.Current);
        if (values.Count == maximumCount && enumerator.MoveNext())
            throw new ArgumentException("Input exceeds its configured finite bound.", parameterName);
        return values.ToImmutable();
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions => definitions;

    private static bool IsValidRate(WindowsD3dRational rate)
        => rate.Denominator != 0 || rate.Numerator == 0;

    private static ImmutableArray<DeviceInformationDefinition> BuildDefinitions(WindowsD3dCapabilityFields fields)
    {
        var result = ImmutableArray.CreateBuilder<DeviceInformationDefinition>(8);
        if ((fields & WindowsD3dCapabilityFields.FeatureLevel) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel);
        if ((fields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllD3dFormatSupportFlags);
        if ((fields & WindowsD3dCapabilityFields.DecoderProfiles) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid);
        if ((fields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat);
        if ((fields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration);
        if ((fields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps);
        if ((fields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps);
        if ((fields & WindowsD3dCapabilityFields.RaytracingTier) != 0)
            result.Add(DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier);
        return result.ToImmutable();
    }

    internal WindowsD3dCapabilityFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var result = WindowsD3dCapabilityFields.None;
        foreach (var item in selection)
        {
            var field = item.Definition == DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel
                ? WindowsD3dCapabilityFields.FeatureLevel
                : item.Definition == DeviceInformationFieldRegistry.GpuAllD3dFormatSupportFlags
                    ? WindowsD3dCapabilityFields.FormatSupportFlags
                    : item.Definition == DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid
                        ? WindowsD3dCapabilityFields.DecoderProfiles
                        : item.Definition == DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat
                            ? WindowsD3dCapabilityFields.DecoderOutputFormats
                        : item.Definition == DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration
                            ? WindowsD3dCapabilityFields.DecoderConfigurations
                        : item.Definition == DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps
                            ? WindowsD3dCapabilityFields.VideoProcessorCaps
                        : item.Definition == DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps
                            ? WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps
                        : item.Definition == DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier
                            ? WindowsD3dCapabilityFields.RaytracingTier
                    : throw new ArgumentException("Unknown D3D capability definition.", nameof(selection));
            if (item.Field.FieldName != item.Definition.Name
                || !ReferenceEquals(item.Definition, DefinitionFor(field))
                || (Fields & field) == 0)
                throw new ArgumentException("D3D selection must use an exact static field inside this scope.", nameof(selection));
            result |= field;
        }
        return result;
    }

    internal static string ApiCode(WindowsD3dApiVersion apiVersion) => apiVersion switch
    {
        WindowsD3dApiVersion.Direct3D11 => "d3d11",
        WindowsD3dApiVersion.Direct3D12 => "d3d12",
        _ => throw new ArgumentOutOfRangeException(nameof(apiVersion))
    };

    internal static bool IsFeatureLevelAllowed(WindowsD3dApiVersion apiVersion, WindowsD3dFeatureLevel level)
        => Enum.IsDefined(level) && (apiVersion switch
        {
        WindowsD3dApiVersion.Direct3D11 => (uint)level >= (uint)WindowsD3dFeatureLevel.Level9_1
                && (uint)level <= (uint)WindowsD3dFeatureLevel.Level12_1,
            WindowsD3dApiVersion.Direct3D12 => (uint)level >= (uint)WindowsD3dFeatureLevel.Level11_0
                && (uint)level <= (uint)WindowsD3dFeatureLevel.Level12_2,
            _ => false
        });

    internal static DeviceInformationDefinition DefinitionFor(WindowsD3dCapabilityFields field) => field switch
    {
        WindowsD3dCapabilityFields.FeatureLevel => DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel,
        WindowsD3dCapabilityFields.FormatSupportFlags => DeviceInformationFieldRegistry.GpuAllD3dFormatSupportFlags,
        WindowsD3dCapabilityFields.DecoderProfiles => DeviceInformationFieldRegistry.GpuAllVideoDecoderProfileGuid,
        WindowsD3dCapabilityFields.DecoderOutputFormats => DeviceInformationFieldRegistry.GpuAllVideoDecoderOutputFormat,
        WindowsD3dCapabilityFields.DecoderConfigurations => DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration,
        WindowsD3dCapabilityFields.VideoProcessorCaps => DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps,
        WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps => DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps,
        WindowsD3dCapabilityFields.RaytracingTier => DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
