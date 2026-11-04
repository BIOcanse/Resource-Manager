using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

public sealed record WindowsD3dFormatSupportRead(WindowsD3dFormat Format, ulong? Flags);

/// <summary>A selected output format candidate for one decoder profile. Null means the native
/// CheckVideoDecoderFormat call failed; false is a successful, explicitly unsupported result.</summary>
public sealed record WindowsD3dVideoDecoderFormatRead(WindowsD3dFormat Format, bool? Supported);

/// <summary>Profile-scoped decoder output-format results. When OutputFormatsComplete is false,
/// the pair query was not complete and OutputFormats must be empty.</summary>
public sealed record WindowsD3dVideoDecoderProfileRead(Guid ProfileGuid,
    bool? OutputFormatsComplete = null,
    ImmutableArray<WindowsD3dVideoDecoderFormatRead> OutputFormats = default);

/// <summary>Every native D3D11_VIDEO_DECODER_CONFIG member, preserving the SDK GUID/UINT/USHORT types.</summary>
public sealed record WindowsD3dVideoDecoderConfig(
    Guid GuidConfigBitstreamEncryption,
    Guid GuidConfigMBcontrolEncryption,
    Guid GuidConfigResidDiffEncryption,
    uint ConfigBitstreamRaw,
    uint ConfigMBcontrolRasterOrder,
    uint ConfigResidDiffHost,
    uint ConfigSpatialResid8,
    uint ConfigResid8Subtraction,
    uint ConfigSpatialHost8or9Clipping,
    uint ConfigSpatialResidInterleaved,
    uint ConfigIntraResidUnsigned,
    uint ConfigResidDiffAccelerator,
    uint ConfigHostInverseScan,
    uint ConfigSpecificIDCT,
    uint Config4GroupedCoefs,
    ushort ConfigMinRenderTargetBuffCount,
    ushort ConfigDecoderSpecific);

public sealed record WindowsD3dVideoDecoderIndexedConfigurationRead(
    uint Index, WindowsD3dVideoDecoderConfig Configuration);

/// <summary>A result for one exact caller-supplied D3D11_VIDEO_DECODER_DESC. Complete with count zero
/// means the native API successfully reported no configurations; incomplete means no partial list may publish.</summary>
public sealed record WindowsD3dVideoDecoderConfigurationsRead(
    WindowsD3dVideoDecoderDescriptor Descriptor,
    bool? ConfigurationsComplete,
    uint? ConfigurationCount,
    ImmutableArray<WindowsD3dVideoDecoderIndexedConfigurationRead> Configurations);

/// <summary>All nine UINT members of D3D11_VIDEO_PROCESSOR_CAPS.</summary>
public sealed record WindowsD3dVideoProcessorCapsValue(
    uint DeviceCaps,
    uint FeatureCaps,
    uint FilterCaps,
    uint InputFormatCaps,
    uint AutoStreamCaps,
    uint StereoCaps,
    uint RateConversionCapsCount,
    uint MaxInputStreams,
    uint MaxStreamStates);

public sealed record WindowsD3dVideoProcessorCapsRead(
    WindowsD3dVideoProcessorDescriptor Descriptor,
    bool? Complete,
    WindowsD3dVideoProcessorCapsValue? Caps);

/// <summary>All five UINT members of D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS.</summary>
public sealed record WindowsD3dRateConversionCapsValue(
    uint PastFrames,
    uint FutureFrames,
    uint ProcessorCaps,
    uint ITelecineCaps,
    uint CustomRateCount);

public sealed record WindowsD3dIndexedRateConversionCapsRead(
    uint Index,
    WindowsD3dRateConversionCapsValue Caps);

public sealed record WindowsD3dVideoProcessorRateConversionCapsRead(
    WindowsD3dVideoProcessorDescriptor Descriptor,
    bool? Complete,
    uint? RateConversionCapsCount,
    ImmutableArray<WindowsD3dIndexedRateConversionCapsRead> RateConversionCaps);

public sealed record WindowsD3dAdapterCapabilityRead(WindowsD3dAdapterIdentity Adapter,
    WindowsD3dApiVersion ApiVersion, WindowsD3dFeatureLevel? FeatureLevel,
    ImmutableArray<WindowsD3dFormatSupportRead> FormatSupport,
    bool? DecoderProfilesComplete = null,
    ImmutableArray<WindowsD3dVideoDecoderProfileRead> DecoderProfiles = default,
    ImmutableArray<WindowsD3dVideoDecoderConfigurationsRead> DecoderConfigurations = default,
    ImmutableArray<WindowsD3dVideoProcessorCapsRead> VideoProcessorCaps = default,
    ImmutableArray<WindowsD3dVideoProcessorRateConversionCapsRead> VideoProcessorRateConversionCaps = default)
{
    /// <summary>Raw D3D12_RAYTRACING_TIER code; zero is supported-query/no-raytracing, null is unavailable or unrequested.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public uint? RaytracingTier { get; init; }
}

public sealed record WindowsD3dCapabilityReadResult(
    ImmutableArray<WindowsD3dAdapterCapabilityRead> Adapters)
{
    internal static WindowsD3dCapabilityReadResult Unavailable(
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selectedFields)
    {
        var adapters = ImmutableArray.CreateBuilder<WindowsD3dAdapterCapabilityRead>(scope.Adapters.Length);
        foreach (var adapter in scope.Adapters)
        {
            ImmutableArray<WindowsD3dFormatSupportRead> formats = (selectedFields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                ? [.. scope.Formats.Select(static format => new WindowsD3dFormatSupportRead(format, null))]
                : [];
            var decoderSelected = (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0;
            adapters.Add(new(adapter, scope.ApiVersion, null, formats,
                decoderSelected ? false : null, [],
                (selectedFields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0
                    ? [.. scope.DecoderConfigurationDescriptors.Select(static descriptor =>
                        new WindowsD3dVideoDecoderConfigurationsRead(descriptor, false, null, []))]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0
                    ? [.. scope.VideoProcessorDescriptors.Select(static descriptor =>
                        new WindowsD3dVideoProcessorCapsRead(descriptor, false, null))]
                    : [],
                (selectedFields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0
                    ? [.. scope.VideoProcessorDescriptors.Select(static descriptor =>
                        new WindowsD3dVideoProcessorRateConversionCapsRead(descriptor, false, null, []))]
                    : []));
        }
        return new(adapters.ToImmutable());
    }
}

/// <summary>Supplies observations for an explicit adapter/API/field request. External providers can
/// be passed directly to the original topology owner; cached reads never call this reader.</summary>
public interface IWindowsD3dCapabilityReader
{
    WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken);
}
