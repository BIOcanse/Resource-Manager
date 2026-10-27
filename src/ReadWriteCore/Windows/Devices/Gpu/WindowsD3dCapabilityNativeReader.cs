using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Read-only capability queries for caller-selected DXGI adapters. It creates only the
/// API device required by the documented query calls; no context, swapchain, command list, or rendering work.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsD3dCapabilityNativeReader : IWindowsD3dCapabilityReader
{
    internal static (int FeatureLevels, int FormatSupport) D3d12PayloadSizes
        => (Marshal.SizeOf<D3d12FeatureDataFeatureLevels>(), Marshal.SizeOf<D3d12FeatureDataFormatSupport>());

    internal static int D3d11FormatSupport2PayloadSize
        => Marshal.SizeOf<D3d11FeatureDataFormatSupport2>();

    internal static (int DecoderDescription, int DecoderConfiguration) D3d11VideoDecoderPayloadSizes
        => (Marshal.SizeOf<D3d11VideoDecoderDesc>(), Marshal.SizeOf<D3d11VideoDecoderConfig>());

    internal static (int SampleWidth, int SampleHeight, int OutputFormat) D3d11VideoDecoderDescriptionOffsets
        => (Marshal.OffsetOf<D3d11VideoDecoderDesc>(nameof(D3d11VideoDecoderDesc.SampleWidth)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoDecoderDesc>(nameof(D3d11VideoDecoderDesc.SampleHeight)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoDecoderDesc>(nameof(D3d11VideoDecoderDesc.OutputFormat)).ToInt32());

    internal static (int FirstUInt, int LastUInt, int MinimumBufferCount, int DecoderSpecific) D3d11VideoDecoderConfigurationOffsets
        => (Marshal.OffsetOf<D3d11VideoDecoderConfig>(nameof(D3d11VideoDecoderConfig.ConfigBitstreamRaw)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoDecoderConfig>(nameof(D3d11VideoDecoderConfig.Config4GroupedCoefs)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoDecoderConfig>(nameof(D3d11VideoDecoderConfig.ConfigMinRenderTargetBuffCount)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoDecoderConfig>(nameof(D3d11VideoDecoderConfig.ConfigDecoderSpecific)).ToInt32());

    internal static (int ProfileCount, int Profile, int Format, int ConfigCount, int Config) D3d11VideoDeviceVTableSlots
        => (D3d11VideoDeviceGetDecoderProfileCountSlot,
            D3d11VideoDeviceGetDecoderProfileSlot, D3d11VideoDeviceCheckDecoderFormatSlot,
            D3d11VideoDeviceGetDecoderConfigCountSlot, D3d11VideoDeviceGetDecoderConfigSlot);

    internal static (int CreateEnumerator, int GetCaps, int GetRateConversionCaps) D3d11VideoProcessorVTableSlots
        => (D3d11VideoDeviceCreateVideoProcessorEnumeratorSlot,
            D3d11VideoProcessorEnumeratorGetCapsSlot,
            D3d11VideoProcessorEnumeratorGetRateConversionCapsSlot);

    internal static (int ContentDescriptor, int ProcessorCaps, int RateConversionCaps) D3d11VideoProcessorPayloadSizes
        => (Marshal.SizeOf<D3d11VideoProcessorContentDesc>(), Marshal.SizeOf<D3d11VideoProcessorCaps>(),
            Marshal.SizeOf<D3d11VideoProcessorRateConversionCaps>());

    internal static (int InputFrameFormat, int InputFrameRate, int InputWidth, int InputHeight,
        int OutputFrameRate, int OutputWidth, int OutputHeight, int Usage) D3d11VideoProcessorContentDescriptorOffsets
        => (Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.InputFrameFormat)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.InputFrameRate)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.InputWidth)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.InputHeight)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.OutputFrameRate)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.OutputWidth)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.OutputHeight)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorContentDesc>(nameof(D3d11VideoProcessorContentDesc.Usage)).ToInt32());

    internal static (int DeviceCaps, int FeatureCaps, int FilterCaps, int InputFormatCaps, int AutoStreamCaps,
        int StereoCaps, int RateConversionCapsCount, int MaxInputStreams, int MaxStreamStates) D3d11VideoProcessorCapsOffsets
        => (Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.DeviceCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.FeatureCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.FilterCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.InputFormatCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.AutoStreamCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.StereoCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.RateConversionCapsCount)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.MaxInputStreams)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorCaps>(nameof(D3d11VideoProcessorCaps.MaxStreamStates)).ToInt32());

    internal static (int PastFrames, int FutureFrames, int ProcessorCaps, int ITelecineCaps, int CustomRateCount)
        D3d11VideoProcessorRateConversionCapsOffsets
        => (Marshal.OffsetOf<D3d11VideoProcessorRateConversionCaps>(nameof(D3d11VideoProcessorRateConversionCaps.PastFrames)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorRateConversionCaps>(nameof(D3d11VideoProcessorRateConversionCaps.FutureFrames)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorRateConversionCaps>(nameof(D3d11VideoProcessorRateConversionCaps.ProcessorCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorRateConversionCaps>(nameof(D3d11VideoProcessorRateConversionCaps.ITelecineCaps)).ToInt32(),
            Marshal.OffsetOf<D3d11VideoProcessorRateConversionCaps>(nameof(D3d11VideoProcessorRateConversionCaps.CustomRateCount)).ToInt32());

    internal static ulong PackD3d11FormatSupport(uint support1, uint support2)
        => ((ulong)support2 << 32) | support1;

    internal static ulong PackD3d12FormatSupport(uint support1, uint support2)
        => ((ulong)support2 << 32) | support1;

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int EInvalidArg = unchecked((int)0x80070057);
    // SDK vtable order: IDXGIFactory1 adds EnumAdapters1 after IDXGIFactory's 12 slots.
    private const int DxgiFactory1EnumAdapters1Slot = 12;
    // SDK vtable order: IDXGIAdapter1::GetDesc1 follows IUnknown, IDXGIObject, and three IDXGIAdapter methods.
    private const int DxgiAdapter1GetDesc1Slot = 10;
    // SDK vtable order: ID3D11Device::CheckFormatSupport follows the 26 preceding device methods.
    private const int D3d11DeviceCheckFormatSupportSlot = 29;
    // SDK vtable order: ID3D11Device::CheckFeatureSupport follows the 30 preceding device methods.
    private const int D3d11DeviceCheckFeatureSupportSlot = 33;
    // Windows SDK 10.0.26100 d3d11.h: ID3D11VideoDevice inherits IUnknown and has
    // GetVideoDecoderProfileCount, GetVideoDecoderProfile, CheckVideoDecoderFormat at slots 11-13.
    private const int D3d11VideoDeviceGetDecoderProfileCountSlot = 11;
    private const int D3d11VideoDeviceGetDecoderProfileSlot = 12;
    private const int D3d11VideoDeviceCheckDecoderFormatSlot = 13;
    // Windows SDK 10.0.26100.0 um/d3d11.h declares these immediately after CheckVideoDecoderFormat.
    private const int D3d11VideoDeviceGetDecoderConfigCountSlot = 14;
    private const int D3d11VideoDeviceGetDecoderConfigSlot = 15;
    // Windows SDK 10.0.26100.0 um/d3d11.h: CreateVideoProcessorEnumerator follows the seven preceding
    // ID3D11VideoDevice methods and the three IUnknown slots.
    private const int D3d11VideoDeviceCreateVideoProcessorEnumeratorSlot = 10;
    // ID3D11VideoProcessorEnumerator inherits seven slots from IUnknown/ID3D11DeviceChild; its content
    // descriptor and format methods are slots 7-8, followed by caps at 9 and rate groups at 10.
    private const int D3d11VideoProcessorEnumeratorGetCapsSlot = 9;
    private const int D3d11VideoProcessorEnumeratorGetRateConversionCapsSlot = 10;
    // SDK vtable order: ID3D12Device::CheckFeatureSupport follows ID3D12Object and six device methods.
    private const int D3d12DeviceCheckFeatureSupportSlot = 13;
    private const uint D3d11DriverTypeUnknown = 0;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11FeatureFormatSupport2 = 3;
    private const uint DxgiAdapterFlagSoftware = 2;
    private const uint D3d12FeatureLevels = 2;
    private const uint D3d12FeatureFormatSupport = 3;
    private const uint D3d12FeatureOptions5 = 27;

    private static readonly Guid IdxgiFactory1Iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Id3d11VideoDeviceIid = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    private static readonly Guid Id3d12DeviceIid = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private static readonly uint[] D3d11Levels =
    [
        (uint)WindowsD3dFeatureLevel.Level12_1,
        (uint)WindowsD3dFeatureLevel.Level12_0,
        (uint)WindowsD3dFeatureLevel.Level11_1,
        (uint)WindowsD3dFeatureLevel.Level11_0,
        (uint)WindowsD3dFeatureLevel.Level10_1,
        (uint)WindowsD3dFeatureLevel.Level10_0,
        (uint)WindowsD3dFeatureLevel.Level9_3,
        (uint)WindowsD3dFeatureLevel.Level9_2,
        (uint)WindowsD3dFeatureLevel.Level9_1
    ];
    private static readonly uint[][] D3d11FeatureLevelAttempts =
    [
        D3d11Levels,
        D3d11Levels[1..],
        D3d11Levels[2..],
        D3d11Levels[3..]
    ];
    private static readonly uint[] D3d12Levels =
    [
        (uint)WindowsD3dFeatureLevel.Level12_2,
        (uint)WindowsD3dFeatureLevel.Level12_1,
        (uint)WindowsD3dFeatureLevel.Level12_0,
        (uint)WindowsD3dFeatureLevel.Level11_1,
        (uint)WindowsD3dFeatureLevel.Level11_0
    ];

    public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (selectedFields == WindowsD3dCapabilityFields.None || (selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Unavailable(scope, selectedFields);

        Dictionary<WindowsD3dAdapterIdentity, IntPtr>? adapters = null;
        try
        {
            adapters = FindExactAdapters(scope, cancellationToken);
            if (adapters is null) return Unavailable(scope, selectedFields);

            var remainingDecoderConfigurationQueries = scope.MaximumVideoDecoderConfigurationQueryCount;
            var remainingVideoProcessorRateConversionQueries = scope.MaximumVideoProcessorRateConversionCapsQueryCount;
            var rows = ImmutableArray.CreateBuilder<WindowsD3dAdapterCapabilityRead>(scope.Adapters.Length);
            foreach (var requested in scope.Adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!adapters.TryGetValue(requested, out var adapter))
                {
                    rows.Add(UnavailableAdapter(scope, selectedFields, requested));
                    continue;
                }

                try
                {
                    rows.Add(ReadAdapter(adapter, scope, selectedFields, requested,
                        ref remainingDecoderConfigurationQueries, ref remainingVideoProcessorRateConversionQueries,
                        cancellationToken));
                }
                catch (DllNotFoundException)
                {
                    rows.Add(UnavailableAdapter(scope, selectedFields, requested));
                }
                catch (EntryPointNotFoundException)
                {
                    rows.Add(UnavailableAdapter(scope, selectedFields, requested));
                }
            }
            return new(rows.ToImmutable());
        }
        catch (DllNotFoundException)
        {
            return Unavailable(scope, selectedFields);
        }
        catch (EntryPointNotFoundException)
        {
            return Unavailable(scope, selectedFields);
        }
        finally
        {
            if (adapters is not null)
                foreach (var adapter in adapters.Values) Marshal.Release(adapter);
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows GPU adapter topology changed")]

    private static Dictionary<WindowsD3dAdapterIdentity, IntPtr>? FindExactAdapters(
        WindowsD3dCapabilityScope scope, CancellationToken cancellationToken)
    {
        var iid = IdxgiFactory1Iid;
        var factoryResult = CreateDXGIFactory1(ref iid, out var factory);
        if (factoryResult < 0 || factory == IntPtr.Zero)
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            return null;
        }

        var requestedByLuid = scope.Adapters.ToDictionary(static item => item.Luid);
        var countsByLuid = new Dictionary<ulong, int>();
        var heldByLuid = new Dictionary<ulong, IntPtr>();
        var complete = false;
        try
        {
            for (uint index = 0; index <= scope.MaximumDxgiAdapterCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = EnumAdapters1(factory, index, out var adapter);
                if (result == DxgiErrorNotFound)
                {
                    complete = true;
                    break;
                }
                if (result < 0 || adapter == IntPtr.Zero || index == scope.MaximumDxgiAdapterCount)
                {
                    if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                    break;
                }

                var keep = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (GetDesc1(adapter, out var description) < 0)
                    {
                        complete = false;
                        break;
                    }

                    var luid = Pack(description.AdapterLuid);
                    if (!requestedByLuid.TryGetValue(luid, out var expected)) continue;
                    var count = countsByLuid.GetValueOrDefault(luid) + 1;
                    countsByLuid[luid] = count;
                    if (count == 1 && (description.Flags & DxgiAdapterFlagSoftware) == 0
                        && description.VendorId == expected.PciVendorId
                        && description.DeviceId == expected.PciDeviceId)
                    {
                        heldByLuid.Add(luid, adapter);
                        keep = true;
                    }
                    else if (count > 1 && heldByLuid.Remove(luid, out var duplicate))
                    {
                        Marshal.Release(duplicate);
                    }
                }
                finally
                {
                    if (!keep) Marshal.Release(adapter);
                }
            }

            if (!complete) return null;
            var exact = new Dictionary<WindowsD3dAdapterIdentity, IntPtr>();
            try
            {
                foreach (var requested in scope.Adapters)
                {
                    if (countsByLuid.GetValueOrDefault(requested.Luid) != 1
                        || !heldByLuid.TryGetValue(requested.Luid, out var adapter)) continue;
                    exact.Add(requested, adapter);
                    heldByLuid.Remove(requested.Luid);
                }
                return exact;
            }
            catch
            {
                foreach (var adapter in exact.Values) Marshal.Release(adapter);
                throw;
            }
        }
        finally
        {
            foreach (var adapter in heldByLuid.Values) Marshal.Release(adapter);
            Marshal.Release(factory);
        }
    }

    private static WindowsD3dAdapterCapabilityRead ReadAdapter(IntPtr adapter,
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selectedFields,
        WindowsD3dAdapterIdentity identity, ref int remainingDecoderConfigurationQueries,
        ref int remainingVideoProcessorRateConversionQueries, CancellationToken cancellationToken)
    {
        if (scope.ApiVersion == WindowsD3dApiVersion.Direct3D11
            && selectedFields == WindowsD3dCapabilityFields.FeatureLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var level = TryReadD3d11FeatureLevel(adapter);
            var reportedFeatureLevel = level is { } code && Enum.IsDefined((WindowsD3dFeatureLevel)code)
                && WindowsD3dCapabilityScope.IsFeatureLevelAllowed(scope.ApiVersion, (WindowsD3dFeatureLevel)code)
                    ? (WindowsD3dFeatureLevel?)code : null;
            return new(identity, scope.ApiVersion, reportedFeatureLevel, []);
        }

        IntPtr device = IntPtr.Zero;
        WindowsD3dFeatureLevel? featureLevel = null;
        uint? raytracingTier = null;
        var formatRows = ImmutableArray.CreateBuilder<WindowsD3dFormatSupportRead>();
        bool? decoderProfilesComplete = null;
        ImmutableArray<WindowsD3dVideoDecoderProfileRead> decoderProfiles = [];
        ImmutableArray<WindowsD3dVideoDecoderConfigurationsRead> decoderConfigurations = [];
        ImmutableArray<WindowsD3dVideoProcessorCapsRead> videoProcessorCaps = [];
        ImmutableArray<WindowsD3dVideoProcessorRateConversionCapsRead> videoProcessorRateConversionCaps = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool deviceCreated;
            uint d3d11FeatureLevelCode = 0;
            if (scope.ApiVersion == WindowsD3dApiVersion.Direct3D11)
            {
                deviceCreated = TryCreateD3d11Device(adapter, out device, out d3d11FeatureLevelCode);
            }
            else
            {
                deviceCreated = scope.ApiVersion == WindowsD3dApiVersion.Direct3D12
                    && TryCreateD3d12Device(adapter, out device);
            }
            if (!deviceCreated || device == IntPtr.Zero)
                return UnavailableAdapter(scope, selectedFields, identity);

            if ((selectedFields & WindowsD3dCapabilityFields.FeatureLevel) != 0)
            {
                var code = scope.ApiVersion == WindowsD3dApiVersion.Direct3D11
                    ? d3d11FeatureLevelCode
                    : TryReadD3d12FeatureLevel(device, cancellationToken);
                if (code is { } value && Enum.IsDefined((WindowsD3dFeatureLevel)value)
                    && WindowsD3dCapabilityScope.IsFeatureLevelAllowed(scope.ApiVersion, (WindowsD3dFeatureLevel)value))
                    featureLevel = (WindowsD3dFeatureLevel)value;
            }

            if ((selectedFields & WindowsD3dCapabilityFields.RaytracingTier) != 0)
                raytracingTier = TryReadD3d12RaytracingTier(device, cancellationToken);

            if ((selectedFields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0)
            {
                foreach (var format in scope.Formats)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var flags = scope.ApiVersion == WindowsD3dApiVersion.Direct3D11
                        ? TryReadD3d11FormatSupport(device, format, cancellationToken)
                        : TryReadD3d12FormatSupport(device, format);
                    formatRows.Add(new(format, flags));
                }
            }
            if ((selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0)
            {
                var decoderRead = ReadD3d11VideoDecoderCapabilities(device, scope, selectedFields, cancellationToken);
                decoderProfilesComplete = decoderRead.Complete;
                decoderProfiles = decoderRead.Profiles;
            }
            if ((selectedFields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0)
            {
                decoderConfigurations = ReadD3d11VideoDecoderConfigurations(device, scope,
                    ref remainingDecoderConfigurationQueries, cancellationToken);
            }
            if ((selectedFields & (WindowsD3dCapabilityFields.VideoProcessorCaps
                | WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps)) != 0)
            {
                var processorRead = ReadD3d11VideoProcessorCapabilities(device, scope, selectedFields,
                    ref remainingVideoProcessorRateConversionQueries, cancellationToken);
                videoProcessorCaps = processorRead.Caps;
                videoProcessorRateConversionCaps = processorRead.RateConversionCaps;
            }
            return new(identity, scope.ApiVersion, featureLevel, formatRows.ToImmutable(),
                decoderProfilesComplete, decoderProfiles, decoderConfigurations,
                videoProcessorCaps, videoProcessorRateConversionCaps) { RaytracingTier = raytracingTier };
        }
        finally
        {
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    internal static (int Size, int RaytracingTier) D3d12Options5Layout
        => (Marshal.SizeOf<D3d12FeatureDataOptions5>(),
            Marshal.OffsetOf<D3d12FeatureDataOptions5>(nameof(D3d12FeatureDataOptions5.RaytracingTier)).ToInt32());

    internal static uint? TryReadD3d12RaytracingTier(IntPtr device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var size = Marshal.SizeOf<D3d12FeatureDataOptions5>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(default(D3d12FeatureDataOptions5), buffer, false);
            var result = DeviceCheckFeatureSupport(device, D3d12FeatureOptions5, buffer, (uint)size);
            cancellationToken.ThrowIfCancellationRequested();
            return result >= 0 ? Marshal.PtrToStructure<D3d12FeatureDataOptions5>(buffer).RaytracingTier : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d12FeatureDataOptions5
    {
        public int SrvOnlyTiledResourceTier3;
        public uint RenderPassesTier;
        public uint RaytracingTier;
    }

    private static bool TryCreateD3d11Device(IntPtr adapter, out IntPtr device, out uint featureLevel)
    {
        foreach (var levels in D3d11FeatureLevelAttempts)
        {
            var result = D3D11CreateDevice(adapter, D3d11DriverTypeUnknown, IntPtr.Zero, 0,
                levels, (uint)levels.Length, D3d11SdkVersion, out device,
                out featureLevel, IntPtr.Zero);
            if (result != EInvalidArg) return result >= 0 && device != IntPtr.Zero;
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
        device = IntPtr.Zero;
        featureLevel = 0;
        return false;
    }

    private static (bool Complete, ImmutableArray<WindowsD3dVideoDecoderProfileRead> Profiles)
        ReadD3d11VideoDecoderCapabilities(IntPtr device, WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var iid = Id3d11VideoDeviceIid;
        var queryResult = Marshal.QueryInterface(device, in iid, out var videoDevice);
        if (queryResult < 0 || videoDevice == IntPtr.Zero)
        {
            if (videoDevice != IntPtr.Zero) Marshal.Release(videoDevice);
            return (false, []);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileCount = VideoDeviceGetDecoderProfileCount(videoDevice);
            if (profileCount > (uint)scope.MaximumVideoDecoderProfileCount)
                return (false, []);

            var outputFormatsSelected = (selectedFields & WindowsD3dCapabilityFields.DecoderOutputFormats) != 0;
            var pairQueryWithinBound = !outputFormatsSelected
                || (ulong)profileCount * (ulong)scope.Formats.Length
                    <= (ulong)scope.MaximumVideoDecoderFormatQueryCount;
            var profileGuids = new Guid[checked((int)profileCount)];
            for (uint index = 0; index < profileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = Guid.Empty;
                var result = VideoDeviceGetDecoderProfile(videoDevice, index, ref profile);
                if (result < 0)
                    return (false, []);
                profileGuids[checked((int)index)] = profile;
            }

            var profileRows = ImmutableArray.CreateBuilder<WindowsD3dVideoDecoderProfileRead>(profileGuids.Length);
            foreach (var profile in profileGuids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!outputFormatsSelected)
                {
                    profileRows.Add(new(profile));
                    continue;
                }
                if (!pairQueryWithinBound)
                {
                    profileRows.Add(new(profile, false, []));
                    continue;
                }

                var formats = ImmutableArray.CreateBuilder<WindowsD3dVideoDecoderFormatRead>(scope.Formats.Length);
                foreach (var format in scope.Formats)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var profileForCall = profile;
                    var result = VideoDeviceCheckDecoderFormat(videoDevice, ref profileForCall,
                        (uint)format, out var supported);
                    cancellationToken.ThrowIfCancellationRequested();
                    formats.Add(new(format, result < 0 ? null : supported != 0));
                }
                profileRows.Add(new(profile, true, formats.ToImmutable()));
            }
            return (true, profileRows.ToImmutable());
        }
        finally { Marshal.Release(videoDevice); }
    }

    private static ImmutableArray<WindowsD3dVideoDecoderConfigurationsRead> ReadD3d11VideoDecoderConfigurations(
        IntPtr device, WindowsD3dCapabilityScope scope, ref int remainingQueries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = ImmutableArray.CreateBuilder<WindowsD3dVideoDecoderConfigurationsRead>(
            scope.DecoderConfigurationDescriptors.Length);
        var iid = Id3d11VideoDeviceIid;
        var queryResult = Marshal.QueryInterface(device, in iid, out var videoDevice);
        if (queryResult < 0 || videoDevice == IntPtr.Zero)
        {
            if (videoDevice != IntPtr.Zero) Marshal.Release(videoDevice);
            foreach (var descriptor in scope.DecoderConfigurationDescriptors)
                results.Add(new(descriptor, false, null, []));
            return results.ToImmutable();
        }

        try
        {
            foreach (var descriptor in scope.DecoderConfigurationDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingQueries == 0)
                {
                    results.Add(new(descriptor, false, null, []));
                    continue;
                }

                remainingQueries--;
                var nativeDescriptor = new D3d11VideoDecoderDesc
                {
                    Guid = descriptor.ProfileGuid,
                    SampleWidth = descriptor.SampleWidth,
                    SampleHeight = descriptor.SampleHeight,
                    OutputFormat = (uint)descriptor.OutputFormat
                };
                var countResult = VideoDeviceGetDecoderConfigCount(videoDevice, ref nativeDescriptor, out var count);
                cancellationToken.ThrowIfCancellationRequested();
                if (countResult < 0)
                {
                    results.Add(new(descriptor, false, null, []));
                    continue;
                }
                if (count > (uint)scope.MaximumVideoDecoderConfigurationCount
                    || count > (uint)remainingQueries)
                {
                    results.Add(new(descriptor, false, count, []));
                    continue;
                }

                var configurations = ImmutableArray.CreateBuilder<WindowsD3dVideoDecoderIndexedConfigurationRead>(
                    checked((int)count));
                var complete = true;
                for (uint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remainingQueries--;
                    var result = VideoDeviceGetDecoderConfig(videoDevice, ref nativeDescriptor,
                        index, out var nativeConfiguration);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result < 0)
                    {
                        complete = false;
                        break;
                    }
                    configurations.Add(new(index, new WindowsD3dVideoDecoderConfig(
                        nativeConfiguration.GuidConfigBitstreamEncryption,
                        nativeConfiguration.GuidConfigMBcontrolEncryption,
                        nativeConfiguration.GuidConfigResidDiffEncryption,
                        nativeConfiguration.ConfigBitstreamRaw,
                        nativeConfiguration.ConfigMBcontrolRasterOrder,
                        nativeConfiguration.ConfigResidDiffHost,
                        nativeConfiguration.ConfigSpatialResid8,
                        nativeConfiguration.ConfigResid8Subtraction,
                        nativeConfiguration.ConfigSpatialHost8or9Clipping,
                        nativeConfiguration.ConfigSpatialResidInterleaved,
                        nativeConfiguration.ConfigIntraResidUnsigned,
                        nativeConfiguration.ConfigResidDiffAccelerator,
                        nativeConfiguration.ConfigHostInverseScan,
                        nativeConfiguration.ConfigSpecificIDCT,
                        nativeConfiguration.Config4GroupedCoefs,
                        nativeConfiguration.ConfigMinRenderTargetBuffCount,
                        nativeConfiguration.ConfigDecoderSpecific)));
                }
                results.Add(complete
                    ? new(descriptor, true, count, configurations.ToImmutable())
                    : new(descriptor, false, count, []));
            }
            return results.ToImmutable();
        }
        finally { Marshal.Release(videoDevice); }
    }

    private static (ImmutableArray<WindowsD3dVideoProcessorCapsRead> Caps,
        ImmutableArray<WindowsD3dVideoProcessorRateConversionCapsRead> RateConversionCaps)
        ReadD3d11VideoProcessorCapabilities(IntPtr device, WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, ref int remainingRateConversionQueries,
            CancellationToken cancellationToken)
    {
        var capsSelected = (selectedFields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0;
        var rateCapsSelected = (selectedFields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0;
        var capsResults = ImmutableArray.CreateBuilder<WindowsD3dVideoProcessorCapsRead>(
            capsSelected ? scope.VideoProcessorDescriptors.Length : 0);
        var rateResults = ImmutableArray.CreateBuilder<WindowsD3dVideoProcessorRateConversionCapsRead>(
            rateCapsSelected ? scope.VideoProcessorDescriptors.Length : 0);
        cancellationToken.ThrowIfCancellationRequested();
        var iid = Id3d11VideoDeviceIid;
        var queryResult = Marshal.QueryInterface(device, in iid, out var videoDevice);
        if (queryResult < 0 || videoDevice == IntPtr.Zero)
        {
            if (videoDevice != IntPtr.Zero) Marshal.Release(videoDevice);
            foreach (var descriptor in scope.VideoProcessorDescriptors)
            {
                if (capsSelected) capsResults.Add(new(descriptor, false, null));
                if (rateCapsSelected) rateResults.Add(new(descriptor, false, null, []));
            }
            return (capsResults.ToImmutable(), rateResults.ToImmutable());
        }

        try
        {
            foreach (var descriptor in scope.VideoProcessorDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativeDescriptor = new D3d11VideoProcessorContentDesc
                {
                    InputFrameFormat = (uint)descriptor.InputFrameFormat,
                    InputFrameRate = new D3d11Rational
                    {
                        Numerator = descriptor.InputFrameRate.Numerator,
                        Denominator = descriptor.InputFrameRate.Denominator
                    },
                    InputWidth = descriptor.InputWidth,
                    InputHeight = descriptor.InputHeight,
                    OutputFrameRate = new D3d11Rational
                    {
                        Numerator = descriptor.OutputFrameRate.Numerator,
                        Denominator = descriptor.OutputFrameRate.Denominator
                    },
                    OutputWidth = descriptor.OutputWidth,
                    OutputHeight = descriptor.OutputHeight,
                    Usage = (uint)descriptor.Usage
                };
                var createResult = VideoDeviceCreateProcessorEnumerator(videoDevice,
                    ref nativeDescriptor, out var processorEnumerator);
                if (createResult < 0 || processorEnumerator == IntPtr.Zero)
                {
                    if (processorEnumerator != IntPtr.Zero) Marshal.Release(processorEnumerator);
                    if (capsSelected) capsResults.Add(new(descriptor, false, null));
                    if (rateCapsSelected) rateResults.Add(new(descriptor, false, null, []));
                    continue;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var capsResult = VideoProcessorEnumeratorGetCaps(processorEnumerator, out var nativeCaps);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (capsResult < 0)
                    {
                        if (capsSelected) capsResults.Add(new(descriptor, false, null));
                        if (rateCapsSelected) rateResults.Add(new(descriptor, false, null, []));
                        continue;
                    }

                    if (capsSelected)
                    {
                        capsResults.Add(new(descriptor, true, new WindowsD3dVideoProcessorCapsValue(
                            nativeCaps.DeviceCaps, nativeCaps.FeatureCaps, nativeCaps.FilterCaps,
                            nativeCaps.InputFormatCaps, nativeCaps.AutoStreamCaps, nativeCaps.StereoCaps,
                            nativeCaps.RateConversionCapsCount, nativeCaps.MaxInputStreams,
                            nativeCaps.MaxStreamStates)));
                    }

                    if (!rateCapsSelected) continue;
                    var count = nativeCaps.RateConversionCapsCount;
                    if (count > (uint)remainingRateConversionQueries)
                    {
                        rateResults.Add(new(descriptor, false, count, []));
                        continue;
                    }

                    var groups = ImmutableArray.CreateBuilder<WindowsD3dIndexedRateConversionCapsRead>(
                        checked((int)count));
                    var complete = true;
                    for (uint index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        remainingRateConversionQueries--;
                        var rateResult = VideoProcessorEnumeratorGetRateConversionCaps(
                            processorEnumerator, index, out var nativeRateCaps);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (rateResult < 0)
                        {
                            complete = false;
                            break;
                        }
                        groups.Add(new(index, new WindowsD3dRateConversionCapsValue(
                            nativeRateCaps.PastFrames, nativeRateCaps.FutureFrames,
                            nativeRateCaps.ProcessorCaps, nativeRateCaps.ITelecineCaps,
                            nativeRateCaps.CustomRateCount)));
                    }
                    rateResults.Add(complete
                        ? new(descriptor, true, count, groups.ToImmutable())
                        : new(descriptor, false, count, []));
                }
                finally { Marshal.Release(processorEnumerator); }
            }
            return (capsResults.ToImmutable(), rateResults.ToImmutable());
        }
        finally { Marshal.Release(videoDevice); }
    }

    private static uint? TryReadD3d11FeatureLevel(IntPtr adapter)
    {
        foreach (var levels in D3d11FeatureLevelAttempts)
        {
            var result = D3D11CreateDeviceForFeatureLevel(adapter, D3d11DriverTypeUnknown, IntPtr.Zero, 0,
                levels, (uint)levels.Length, D3d11SdkVersion, IntPtr.Zero,
                out var featureLevel, IntPtr.Zero);
            if (result == EInvalidArg) continue;
            return result < 0 ? null : featureLevel;
        }
        return null;
    }

    private static bool TryCreateD3d12Device(IntPtr adapter, out IntPtr device)
    {
        var iid = Id3d12DeviceIid;
        return D3D12CreateDevice(adapter, (uint)WindowsD3dFeatureLevel.Level11_0,
            ref iid, out device) >= 0 && device != IntPtr.Zero;
    }

    private static uint? TryReadD3d12FeatureLevel(IntPtr device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pinned = GCHandle.Alloc(D3d12Levels, GCHandleType.Pinned);
        var buffer = IntPtr.Zero;
        try
        {
            var data = new D3d12FeatureDataFeatureLevels
            {
                NumFeatureLevels = (uint)D3d12Levels.Length,
                FeatureLevelsRequested = pinned.AddrOfPinnedObject(),
                MaxSupportedFeatureLevel = 0
            };
            var size = Marshal.SizeOf<D3d12FeatureDataFeatureLevels>();
            buffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(data, buffer, false);
            var result = DeviceCheckFeatureSupport(device, D3d12FeatureLevels, buffer, (uint)size);
            cancellationToken.ThrowIfCancellationRequested();
            return result < 0 ? null : Marshal.PtrToStructure<D3d12FeatureDataFeatureLevels>(buffer)
                .MaxSupportedFeatureLevel;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            pinned.Free();
        }
    }

    private static ulong? TryReadD3d11FormatSupport(IntPtr device, WindowsD3dFormat format,
        CancellationToken cancellationToken)
    {
        var support1 = 0U;
        var result = DeviceCheckFormatSupport(device, (uint)format, ref support1);
        if (result < 0) return null;
        cancellationToken.ThrowIfCancellationRequested();

        var size = Marshal.SizeOf<D3d11FeatureDataFormatSupport2>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3d11FeatureDataFormatSupport2 { InFormat = (uint)format }, buffer, false);
            result = DeviceCheckD3d11FeatureSupport(device, D3d11FeatureFormatSupport2, buffer, (uint)size);
            if (result < 0) return null;
            var support2 = Marshal.PtrToStructure<D3d11FeatureDataFormatSupport2>(buffer).OutFormatSupport2;
            return PackD3d11FormatSupport(support1, support2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ulong? TryReadD3d12FormatSupport(IntPtr device, WindowsD3dFormat format)
    {
        var size = Marshal.SizeOf<D3d12FeatureDataFormatSupport>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3d12FeatureDataFormatSupport { Format = (uint)format }, buffer, false);
            var result = DeviceCheckFeatureSupport(device, D3d12FeatureFormatSupport, buffer, (uint)size);
            if (result < 0) return null;
            var data = Marshal.PtrToStructure<D3d12FeatureDataFormatSupport>(buffer);
            return PackD3d12FormatSupport(data.Support1, data.Support2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static WindowsD3dCapabilityReadResult Unavailable(
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selectedFields)
        => new([.. scope.Adapters.Select(adapter => UnavailableAdapter(scope, selectedFields, adapter))]);

    private static WindowsD3dAdapterCapabilityRead UnavailableAdapter(
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selectedFields,
        WindowsD3dAdapterIdentity adapter)
        => new(adapter, scope.ApiVersion, null,
            (selectedFields & WindowsD3dCapabilityFields.FormatSupportFlags) != 0
                ? [.. scope.Formats.Select(static format => new WindowsD3dFormatSupportRead(format, null))]
                : [],
            (selectedFields & (WindowsD3dCapabilityFields.DecoderProfiles
                | WindowsD3dCapabilityFields.DecoderOutputFormats)) != 0 ? false : null, [],
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
                : []);

    private static ulong Pack(DxgiLuid luid) => ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;

    private static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
        => Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(VTableEntry(factory, DxgiFactory1EnumAdapters1Slot))
            (factory, index, out adapter);

    private static int GetDesc1(IntPtr adapter, out DxgiAdapterDesc1 description)
        => Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(VTableEntry(adapter, DxgiAdapter1GetDesc1Slot))
            (adapter, out description);

    private static int DeviceCheckFormatSupport(IntPtr device, uint format, ref uint support)
        => Marshal.GetDelegateForFunctionPointer<D3d11CheckFormatSupportDelegate>(
            VTableEntry(device, D3d11DeviceCheckFormatSupportSlot))(device, format, ref support);

    private static int DeviceCheckD3d11FeatureSupport(IntPtr device, uint feature, IntPtr data, uint dataSize)
        => Marshal.GetDelegateForFunctionPointer<D3d11CheckFeatureSupportDelegate>(
            VTableEntry(device, D3d11DeviceCheckFeatureSupportSlot))(device, feature, data, dataSize);

    private static uint VideoDeviceGetDecoderProfileCount(IntPtr videoDevice)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceGetDecoderProfileCountDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceGetDecoderProfileCountSlot))(videoDevice);

    private static int VideoDeviceGetDecoderProfile(IntPtr videoDevice, uint index, ref Guid profile)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceGetDecoderProfileDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceGetDecoderProfileSlot))(videoDevice, index, ref profile);

    private static int VideoDeviceCheckDecoderFormat(IntPtr videoDevice, ref Guid profile, uint format, out int supported)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceCheckDecoderFormatDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceCheckDecoderFormatSlot))(videoDevice, ref profile, format, out supported);

    private static int VideoDeviceGetDecoderConfigCount(IntPtr videoDevice,
        ref D3d11VideoDecoderDesc descriptor, out uint count)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceGetDecoderConfigCountDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceGetDecoderConfigCountSlot))(
                videoDevice, ref descriptor, out count);

    private static int VideoDeviceGetDecoderConfig(IntPtr videoDevice,
        ref D3d11VideoDecoderDesc descriptor, uint index, out D3d11VideoDecoderConfig configuration)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceGetDecoderConfigDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceGetDecoderConfigSlot))(
                videoDevice, ref descriptor, index, out configuration);

    private static int VideoDeviceCreateProcessorEnumerator(IntPtr videoDevice,
        ref D3d11VideoProcessorContentDesc descriptor, out IntPtr processorEnumerator)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoDeviceCreateProcessorEnumeratorDelegate>(
            VTableEntry(videoDevice, D3d11VideoDeviceCreateVideoProcessorEnumeratorSlot))(
                videoDevice, ref descriptor, out processorEnumerator);

    private static int VideoProcessorEnumeratorGetCaps(IntPtr processorEnumerator,
        out D3d11VideoProcessorCaps caps)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoProcessorEnumeratorGetCapsDelegate>(
            VTableEntry(processorEnumerator, D3d11VideoProcessorEnumeratorGetCapsSlot))(
                processorEnumerator, out caps);

    private static int VideoProcessorEnumeratorGetRateConversionCaps(IntPtr processorEnumerator,
        uint typeIndex, out D3d11VideoProcessorRateConversionCaps caps)
        => Marshal.GetDelegateForFunctionPointer<D3d11VideoProcessorEnumeratorGetRateConversionCapsDelegate>(
            VTableEntry(processorEnumerator, D3d11VideoProcessorEnumeratorGetRateConversionCapsSlot))(
                processorEnumerator, typeIndex, out caps);

    private static int DeviceCheckFeatureSupport(IntPtr device, uint feature, IntPtr data, uint dataSize)
        => Marshal.GetDelegateForFunctionPointer<D3d12CheckFeatureSupportDelegate>(
            VTableEntry(device, D3d12DeviceCheckFeatureSupportSlot))(device, feature, data, dataSize);

    private static IntPtr VTableEntry(IntPtr instance, int slot)
    {
        if (instance == IntPtr.Zero) throw new ArgumentNullException(nameof(instance));
        var vtable = Marshal.ReadIntPtr(instance);
        if (vtable == IntPtr.Zero) throw new InvalidOperationException("The COM instance has no vtable.");
        return Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] uint[] featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device,
        out uint featureLevel, IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true, EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDeviceForFeatureLevel(IntPtr adapter, uint driverType,
        IntPtr software, uint flags,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] uint[] featureLevels, uint featureLevelCount,
        uint sdkVersion, IntPtr deviceOutput, out uint featureLevel, IntPtr immediateContext);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(IntPtr adapter, uint minimumFeatureLevel,
        ref Guid riid, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out DxgiAdapterDesc1 description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11CheckFormatSupportDelegate(IntPtr device, uint format, ref uint support);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11CheckFeatureSupportDelegate(IntPtr device, uint feature, IntPtr data, uint dataSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint D3d11VideoDeviceGetDecoderProfileCountDelegate(IntPtr videoDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoDeviceGetDecoderProfileDelegate(IntPtr videoDevice, uint index, ref Guid profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoDeviceCheckDecoderFormatDelegate(IntPtr videoDevice, ref Guid profile,
        uint format, out int supported);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoDeviceGetDecoderConfigCountDelegate(IntPtr videoDevice,
        ref D3d11VideoDecoderDesc descriptor, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoDeviceGetDecoderConfigDelegate(IntPtr videoDevice,
        ref D3d11VideoDecoderDesc descriptor, uint index, out D3d11VideoDecoderConfig configuration);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoDeviceCreateProcessorEnumeratorDelegate(IntPtr videoDevice,
        ref D3d11VideoProcessorContentDesc descriptor, out IntPtr processorEnumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoProcessorEnumeratorGetCapsDelegate(IntPtr processorEnumerator,
        out D3d11VideoProcessorCaps caps);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3d11VideoProcessorEnumeratorGetRateConversionCapsDelegate(IntPtr processorEnumerator,
        uint typeIndex, out D3d11VideoProcessorRateConversionCaps caps);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int D3d12CheckFeatureSupportDelegate(IntPtr device, uint feature, IntPtr data, uint dataSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public DxgiLuid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d12FeatureDataFeatureLevels
    {
        public uint NumFeatureLevels;
        public IntPtr FeatureLevelsRequested;
        public uint MaxSupportedFeatureLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11FeatureDataFormatSupport2
    {
        public uint InFormat;
        public uint OutFormatSupport2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d12FeatureDataFormatSupport
    {
        public uint Format;
        public uint Support1;
        public uint Support2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public uint OutputFormat;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11VideoDecoderConfig
    {
        public Guid GuidConfigBitstreamEncryption;
        public Guid GuidConfigMBcontrolEncryption;
        public Guid GuidConfigResidDiffEncryption;
        public uint ConfigBitstreamRaw;
        public uint ConfigMBcontrolRasterOrder;
        public uint ConfigResidDiffHost;
        public uint ConfigSpatialResid8;
        public uint ConfigResid8Subtraction;
        public uint ConfigSpatialHost8or9Clipping;
        public uint ConfigSpatialResidInterleaved;
        public uint ConfigIntraResidUnsigned;
        public uint ConfigResidDiffAccelerator;
        public uint ConfigHostInverseScan;
        public uint ConfigSpecificIDCT;
        public uint Config4GroupedCoefs;
        public ushort ConfigMinRenderTargetBuffCount;
        public ushort ConfigDecoderSpecific;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11VideoProcessorContentDesc
    {
        public uint InputFrameFormat;
        public D3d11Rational InputFrameRate;
        public uint InputWidth;
        public uint InputHeight;
        public D3d11Rational OutputFrameRate;
        public uint OutputWidth;
        public uint OutputHeight;
        public uint Usage;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11VideoProcessorCaps
    {
        public uint DeviceCaps;
        public uint FeatureCaps;
        public uint FilterCaps;
        public uint InputFormatCaps;
        public uint AutoStreamCaps;
        public uint StereoCaps;
        public uint RateConversionCapsCount;
        public uint MaxInputStreams;
        public uint MaxStreamStates;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct D3d11VideoProcessorRateConversionCaps
    {
        public uint PastFrames;
        public uint FutureFrames;
        public uint ProcessorCaps;
        public uint ITelecineCaps;
        public uint CustomRateCount;
    }

}
