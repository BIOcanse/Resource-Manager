using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsDisplayPathTopologyReader
{
    private const uint QueryAllPaths = 0x00000001;
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const uint DisplayConfigPathActive = 0x00000001;
    private const uint InvalidModeInfoIndex = 0xFFFFFFFF;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetAdapterName = 4;
    private const int GetAdvancedColorInfo = 9;
    private const int GetSdrWhiteLevel = 11;
    private const int GetAdvancedColorInfo2 = 15;
    private const int AdvancedColorModeHdr = 2;
    private const uint AdvancedColorSupportedFlag = 0x01;
    private const uint AdvancedColorActiveFlag = 0x02;
    private const uint LegacyWideColorEnforcedFlag = 0x04;
    private const uint AdvancedColorLimitedByPolicyFlag = 0x08;
    private const uint HighDynamicRangeSupportedFlag = 0x10;
    private const uint HighDynamicRangeUserEnabledFlag = 0x20;
    private const int ModeInfoTypeSource = 1;
    public static DeviceTopologyDisplayPathSnapshot ReadSnapshot()
    {
        return ReadSnapshot(
            QueryOnlyActivePaths,
            includeInactivePaths: false,
            "QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)");
    }

    private static DeviceTopologyDisplayPathSnapshot ReadSnapshot(
        uint queryFlags,
        bool includeInactivePaths,
        string operationName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DeviceTopologyDisplayPathSnapshot(
                [],
                $"{operationName} is only supported on Windows.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(queryFlags, out var pathCount, out var modeCount);
            if (result != ErrorSuccess)
            {
                return new DeviceTopologyDisplayPathSnapshot([], DescribeError("GetDisplayConfigBufferSizes", result));
            }

            if (pathCount == 0)
            {
                return new DeviceTopologyDisplayPathSnapshot([], null);
            }

            var paths = new DisplayConfigPathInfo[checked((int)pathCount)];
            var modes = new DisplayConfigModeInfo[checked((int)Math.Max(modeCount, 1u))];
            var returnedPathCount = pathCount;
            var returnedModeCount = modeCount;
            result = QueryDisplayConfig(
                queryFlags,
                ref returnedPathCount,
                paths,
                ref returnedModeCount,
                modes,
                IntPtr.Zero);
            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            if (result != ErrorSuccess)
            {
                return new DeviceTopologyDisplayPathSnapshot([], DescribeError("QueryDisplayConfig", result));
            }

            var dxgiSnapshot = WindowsDxgiDisplayCapabilityReader.ReadAll();
            var output = paths
                .Take(checked((int)returnedPathCount))
                .Where(path => includeInactivePaths
                    || ((path.Flags & DisplayConfigPathActive) != 0 && path.TargetInfo.TargetAvailable != 0))
                .Select(path => CreatePath(
                    path,
                    modes,
                    returnedModeCount,
                    dxgiSnapshot.Capabilities,
                    dxgiSnapshot.Complete))
                .ToArray();
            return new DeviceTopologyDisplayPathSnapshot(output, null);
        }

        return new DeviceTopologyDisplayPathSnapshot(
            [],
            $"{operationName} kept changing while it was read; QueryDisplayConfig could not return a stable snapshot.");
    }

    private static DeviceTopologyDisplayPath CreatePath(
        DisplayConfigPathInfo path,
        IReadOnlyList<DisplayConfigModeInfo> modes,
        uint returnedModeCount,
        IReadOnlyDictionary<string, DeviceTopologyDxgiDisplayCapabilities> dxgiCapabilities,
        bool dxgiObservationComplete)
    {
        var targetName = ReadTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        var adapterName = ReadAdapterName(path.TargetInfo.AdapterId);
        var sourceName = ReadSourceName(path.SourceInfo.AdapterId, path.SourceInfo.Id);
        var sourceMode = ReadSourceMode(path.SourceInfo.ModeInfoIndex, modes, returnedModeCount);
        var technology = targetName?.OutputTechnology ?? path.TargetInfo.OutputTechnology;
        var advancedColor = ReadAdvancedColor(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        var sdrWhiteLevelNits = ReadSdrWhiteLevelNits(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        var monitorDevicePath = Clean(targetName?.MonitorDevicePath);
        var edid = WindowsMonitorEdidReader.Read(NormalizeMonitorDevicePath(monitorDevicePath));
        var sourceDeviceName = Clean(sourceName?.ViewGdiDeviceName);
        var dxgi = sourceDeviceName is not null
            && dxgiCapabilities.TryGetValue(sourceDeviceName, out var capability)
                ? capability
                : null;
        return new DeviceTopologyDisplayPath(
            path.TargetInfo.AdapterId.HighPart,
            path.TargetInfo.AdapterId.LowPart,
            path.SourceInfo.Id,
            path.TargetInfo.Id,
            technology,
            targetName?.ConnectorInstance ?? 0,
            Clean(targetName?.MonitorFriendlyDeviceName),
            monitorDevicePath,
            sourceMode?.Width,
            sourceMode?.Height,
            path.TargetInfo.RefreshRate.Numerator,
            path.TargetInfo.RefreshRate.Denominator,
            Active: (path.Flags & DisplayConfigPathActive) != 0,
            TargetAvailable: path.TargetInfo.TargetAvailable != 0,
            AdapterDevicePath: Clean(adapterName?.AdapterDevicePath) ?? string.Empty,
            AdvancedColor: advancedColor,
            SdrWhiteLevelNits: sdrWhiteLevelNits,
            Edid: edid.Capabilities,
            SourceDeviceName: sourceDeviceName,
            Dxgi: dxgi,
            PositionX: sourceMode?.PositionX,
            PositionY: sourceMode?.PositionY,
            EdidObservationComplete: edid.Complete,
            DxgiObservationComplete: dxgiObservationComplete);
    }

    private static DisplayConfigAdapterName? ReadAdapterName(DisplayConfigLuid adapterId)
    {
        var value = new DisplayConfigAdapterName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetAdapterName,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigAdapterName>()),
                AdapterId = adapterId,
                Id = 0
            },
            AdapterDevicePath = string.Empty
        };
        return DisplayConfigGetDeviceInfo(ref value) == ErrorSuccess ? value : null;
    }

    private static DisplayConfigTargetDeviceName? ReadTargetName(DisplayConfigLuid adapterId, uint targetId)
    {
        var value = new DisplayConfigTargetDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetTargetName,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>()),
                AdapterId = adapterId,
                Id = targetId
            },
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty
        };
        return DisplayConfigGetDeviceInfo(ref value) == ErrorSuccess ? value : null;
    }

    private static DisplayConfigSourceDeviceName? ReadSourceName(DisplayConfigLuid adapterId, uint sourceId)
    {
        var value = new DisplayConfigSourceDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetSourceName,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                AdapterId = adapterId,
                Id = sourceId
            },
            ViewGdiDeviceName = string.Empty
        };
        return DisplayConfigGetDeviceInfo(ref value) == ErrorSuccess ? value : null;
    }

    private static DeviceTopologyDisplayAdvancedColor? ReadAdvancedColor(
        DisplayConfigLuid adapterId,
        uint targetId)
    {
        var current = new DisplayConfigGetAdvancedColorInfo2
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetAdvancedColorInfo2,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo2>()),
                AdapterId = adapterId,
                Id = targetId
            }
        };
        if (DisplayConfigGetDeviceInfo(ref current) == ErrorSuccess)
        {
            var active = (current.Flags & AdvancedColorActiveFlag) != 0;
            return new DeviceTopologyDisplayAdvancedColor(
                AdvancedColorSupported: (current.Flags & AdvancedColorSupportedFlag) != 0,
                AdvancedColorEnabled: active,
                WideColorEnforced: false,
                AdvancedColorForceDisabled: (current.Flags & AdvancedColorLimitedByPolicyFlag) != 0,
                ColorEncoding: DescribeColorEncoding(current.ColorEncoding),
                BitsPerColorChannel: current.BitsPerColorChannel == 0 ? null : current.BitsPerColorChannel,
                HighDynamicRangeSupported: (current.Flags & HighDynamicRangeSupportedFlag) != 0,
                HighDynamicRangeUserEnabled: (current.Flags & HighDynamicRangeUserEnabledFlag) != 0,
                HighDynamicRangeActive: active && current.ActiveColorMode == AdvancedColorModeHdr);
        }

        var legacy = new DisplayConfigGetAdvancedColorInfo
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetAdvancedColorInfo,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>()),
                AdapterId = adapterId,
                Id = targetId
            }
        };
        if (DisplayConfigGetDeviceInfo(ref legacy) != ErrorSuccess)
        {
            return null;
        }

        var legacyEnabled = (legacy.Flags & AdvancedColorActiveFlag) != 0;
        var wideColorEnforced = (legacy.Flags & LegacyWideColorEnforcedFlag) != 0;
        return new DeviceTopologyDisplayAdvancedColor(
            AdvancedColorSupported: (legacy.Flags & AdvancedColorSupportedFlag) != 0,
            AdvancedColorEnabled: legacyEnabled,
            WideColorEnforced: wideColorEnforced,
            AdvancedColorForceDisabled: (legacy.Flags & AdvancedColorLimitedByPolicyFlag) != 0,
            ColorEncoding: DescribeColorEncoding(legacy.ColorEncoding),
            BitsPerColorChannel: legacy.BitsPerColorChannel == 0 ? null : legacy.BitsPerColorChannel,
            HighDynamicRangeSupported: (legacy.Flags & AdvancedColorSupportedFlag) != 0,
            HighDynamicRangeUserEnabled: legacyEnabled,
            HighDynamicRangeActive: legacyEnabled && !wideColorEnforced);
    }

    private static double? ReadSdrWhiteLevelNits(DisplayConfigLuid adapterId, uint targetId)
    {
        var value = new DisplayConfigSdrWhiteLevel
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetSdrWhiteLevel,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigSdrWhiteLevel>()),
                AdapterId = adapterId,
                Id = targetId
            }
        };
        return DisplayConfigGetDeviceInfo(ref value) == ErrorSuccess && value.SdrWhiteLevel > 0
            ? Math.Round(value.SdrWhiteLevel / 1000d * 80d, 1)
            : null;
    }

    internal static string? DescribeColorEncoding(int encoding)
    {
        return encoding switch
        {
            0 => "RGB",
            1 => "YCbCr 4:4:4",
            2 => "YCbCr 4:2:2",
            3 => "YCbCr 4:2:0",
            4 => "Intensity",
            _ => null
        };
    }

    private static DisplayConfigSourceMode? ReadSourceMode(
        uint modeInfoIndex,
        IReadOnlyList<DisplayConfigModeInfo> modes,
        uint returnedModeCount)
    {
        if (modeInfoIndex == InvalidModeInfoIndex || modeInfoIndex >= returnedModeCount || modeInfoIndex >= modes.Count)
        {
            return null;
        }

        var mode = modes[checked((int)modeInfoIndex)];
        return mode.InfoType == ModeInfoTypeSource ? mode.ModeInfo.SourceMode : null;
    }

    /// <summary>返回 <see cref="DeviceDisplayOutputTechnologies"/> 里的取值，措辞由前端出。</summary>
    internal static string DescribeOutputTechnology(int technology)
    {
        return technology switch
        {
            -1 => DeviceDisplayOutputTechnologies.Other,
            0 => DeviceDisplayOutputTechnologies.Vga,
            1 => DeviceDisplayOutputTechnologies.SVideo,
            2 => DeviceDisplayOutputTechnologies.CompositeVideo,
            3 => DeviceDisplayOutputTechnologies.ComponentVideo,
            4 => DeviceDisplayOutputTechnologies.Dvi,
            5 => DeviceDisplayOutputTechnologies.Hdmi,
            6 => DeviceDisplayOutputTechnologies.Lvds,
            8 => DeviceDisplayOutputTechnologies.DJpn,
            9 => DeviceDisplayOutputTechnologies.Sdi,
            10 => DeviceDisplayOutputTechnologies.DisplayPortExternal,
            11 => DeviceDisplayOutputTechnologies.DisplayPortEmbedded,
            12 => DeviceDisplayOutputTechnologies.UdiExternal,
            13 => DeviceDisplayOutputTechnologies.UdiEmbedded,
            14 => DeviceDisplayOutputTechnologies.SdtvDongle,
            15 => DeviceDisplayOutputTechnologies.Miracast,
            16 => DeviceDisplayOutputTechnologies.IndirectWired,
            17 => DeviceDisplayOutputTechnologies.IndirectVirtual,
            18 => DeviceDisplayOutputTechnologies.DisplayPortUsb4Tunnel,
            unchecked((int)0x80000000) => DeviceDisplayOutputTechnologies.Internal,
            _ => DeviceDisplayOutputTechnologies.Unknown
        };
    }

    internal static string ResolveConnectorKind(int technology)
    {
        return technology switch
        {
            0 => DeviceConnectorKinds.Vga,
            4 => DeviceConnectorKinds.Dvi,
            5 => DeviceConnectorKinds.Hdmi,
            10 or 18 => DeviceConnectorKinds.DisplayPort,
            6 or 11 or 13 or unchecked((int)0x80000000) => DeviceConnectorKinds.InternalDisplay,
            15 or 17 => DeviceConnectorKinds.WirelessDisplay,
            _ => DeviceConnectorKinds.Generic
        };
    }

    internal static bool IsInternalOutput(int technology)
    {
        return technology is 6 or 11 or 13 or unchecked((int)0x80000000);
    }

    internal static bool IsUserConnectableOutput(int technology)
    {
        return technology is 0 or 1 or 2 or 3 or 4 or 5 or 8 or 9 or 10 or 12 or 14 or 16 or 18;
    }

    /// <summary>读不出刷新率时返回 null，占位文案由前端出。</summary>
    internal static string? FormatRefreshRate(uint numerator, uint denominator)
    {
        if (numerator == 0 || denominator == 0)
        {
            return null;
        }

        var value = numerator / (double)denominator;
        return $"{value.ToString(Math.Abs(value - Math.Round(value)) < 0.005 ? "0" : "0.##", CultureInfo.InvariantCulture)} Hz";
    }

    internal static string? FormatResolution(uint? width, uint? height)
    {
        return width is > 0 && height is > 0 ? $"{width} x {height}" : null;
    }

    internal static string NormalizeMonitorDevicePath(string? devicePath)
    {
        var value = Clean(devicePath);
        if (value is null)
        {
            return string.Empty;
        }

        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        var parts = value.Split('#');
        return parts.Length >= 3 && parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? $@"DISPLAY\{parts[1]}\{parts[2]}".ToUpperInvariant()
            : value.Replace('#', '\\').ToUpperInvariant();
    }

    internal static (int PathInfo, int ModeInfo, int TargetName) GetNativeStructureSizes()
    {
        return (
            Marshal.SizeOf<DisplayConfigPathInfo>(),
            Marshal.SizeOf<DisplayConfigModeInfo>(),
            Marshal.SizeOf<DisplayConfigTargetDeviceName>());
    }

    internal static (int SourceName, int AdvancedColor, int SdrWhiteLevel) GetCapabilityStructureSizes()
    {
        return (
            Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
            Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
            Marshal.SizeOf<DisplayConfigSdrWhiteLevel>());
    }

    private static string DescribeError(string operation, int result)
    {
        return $"{operation} failed: Win32 {result} ({new System.ComponentModel.Win32Exception(result).Message})";
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\0');
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo2 requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public DisplayConfigRational RefreshRate;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint Width;
        public uint Height;
        public int PixelFormat;
        public int PositionX;
        public int PositionY;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DisplayConfigModeInfoUnion
    {
        [FieldOffset(0)]
        public DisplayConfigSourceMode SourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public int InfoType;
        public uint Id;
        public DisplayConfigLuid AdapterId;
        public DisplayConfigModeInfoUnion ModeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public DisplayConfigLuid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigAdapterName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string AdapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo2
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
        public int ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }
}

internal sealed record DeviceTopologyDisplayPathSnapshot(
    IReadOnlyList<DeviceTopologyDisplayPath> Paths,
    string? Error);

internal sealed record DeviceTopologyDisplayPath(
    int AdapterHighPart,
    uint AdapterLowPart,
    uint SourceId,
    uint TargetId,
    int OutputTechnology,
    uint ConnectorInstance,
    string? MonitorFriendlyName,
    string? MonitorDevicePath,
    uint? Width,
    uint? Height,
    uint RefreshRateNumerator,
    uint RefreshRateDenominator,
    bool Active,
    bool TargetAvailable,
    string AdapterDevicePath,
    DeviceTopologyDisplayAdvancedColor? AdvancedColor = null,
    double? SdrWhiteLevelNits = null,
    DeviceTopologyEdidCapabilities? Edid = null,
    string? SourceDeviceName = null,
    DeviceTopologyDxgiDisplayCapabilities? Dxgi = null,
    int? PositionX = null,
    int? PositionY = null,
    bool EdidObservationComplete = true,
    bool DxgiObservationComplete = true);

internal sealed record DeviceTopologyDisplayAdvancedColor(
    bool AdvancedColorSupported,
    bool AdvancedColorEnabled,
    bool WideColorEnforced,
    bool AdvancedColorForceDisabled,
    string? ColorEncoding,
    uint? BitsPerColorChannel,
    bool HighDynamicRangeSupported = false,
    bool HighDynamicRangeUserEnabled = false,
    bool HighDynamicRangeActive = false);
