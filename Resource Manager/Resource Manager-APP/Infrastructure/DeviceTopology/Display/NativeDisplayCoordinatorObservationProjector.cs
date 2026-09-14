using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal sealed record NativeDisplaySourceBatch(
    NativeDisplaySource Source,
    NativeDisplaySourceStatus Status,
    NativeDisplayFact[] Facts,
    NativeDisplayTextInput[] Texts,
    byte[] TextBytes);

internal static class NativeDisplayCoordinatorObservationProjector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static IReadOnlyList<NativeDisplaySourceBatch> Create(
        IReadOnlyList<DeviceTopologyDisplayPath> paths,
        IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> profiles,
        NativeDisplayCoordinatorPayloadCatalog payloads,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(payloads);
        var observedAt = capturedAt.ToUnixTimeMilliseconds();
        return
        [
            CreateDisplayConfig(paths, payloads, observedAt),
            CreateDxgi(paths, payloads, observedAt),
            CreateEdid(paths, payloads, observedAt),
            Empty(NativeDisplaySource.SetupApiMonitor, NativeDisplaySourceStatus.Unsupported),
            CreateOemProfiles(profiles, payloads, observedAt)
        ];
    }

    private static NativeDisplaySourceBatch CreateDisplayConfig(
        IReadOnlyList<DeviceTopologyDisplayPath> paths,
        NativeDisplayCoordinatorPayloadCatalog payloads,
        long observedAt)
    {
        var builder = new SourceBuilder(NativeDisplaySource.DisplayConfig);
        foreach (var path in paths)
        {
            var target = CreateTargetIdentity(path);
            var fact = CreateBaseFact(
                builder,
                path,
                payloads.AddDisplayPath(path),
                target,
                observedAt);
            if (!string.IsNullOrWhiteSpace(path.AdapterDevicePath))
            {
                fact.MatchTextIndex = builder.AddText(
                    NativeDisplayTextKind.AdapterDevicePath,
                    path.AdapterDevicePath);
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.AdapterDevicePath;
            }
            builder.Add(fact);
        }
        return builder.Build(NativeDisplaySourceStatus.Complete);
    }

    private static NativeDisplaySourceBatch CreateDxgi(
        IReadOnlyList<DeviceTopologyDisplayPath> paths,
        NativeDisplayCoordinatorPayloadCatalog payloads,
        long observedAt)
    {
        if (paths.Any(static path => !path.DxgiObservationComplete))
        {
            return Empty(NativeDisplaySource.Dxgi, NativeDisplaySourceStatus.Unavailable);
        }

        var builder = new SourceBuilder(NativeDisplaySource.Dxgi);
        foreach (var path in paths.Where(static path => path.Dxgi is not null))
        {
            var fact = CreateIdentityFact(
                builder,
                path,
                payloads.AddEvidence(StrictUtf8.GetBytes($"dxgi:{path.SourceDeviceName}:{path.TargetId}")),
                observedAt);
            var dxgi = path.Dxgi!;
            if (dxgi.BitsPerColorChannel is > 0)
            {
                fact.BitsPerColorChannel = dxgi.BitsPerColorChannel.Value;
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.BitsPerColorChannel;
            }
            SetLuminance(
                ref fact,
                dxgi.MinimumLuminanceNits,
                dxgi.MaximumLuminanceNits,
                dxgi.MaximumFullFrameLuminanceNits);
            builder.Add(fact);
        }
        return builder.Build(NativeDisplaySourceStatus.Complete);
    }

    private static NativeDisplaySourceBatch CreateEdid(
        IReadOnlyList<DeviceTopologyDisplayPath> paths,
        NativeDisplayCoordinatorPayloadCatalog payloads,
        long observedAt)
    {
        if (paths.Any(static path => !path.EdidObservationComplete))
        {
            return Empty(NativeDisplaySource.Edid, NativeDisplaySourceStatus.Unavailable);
        }

        var builder = new SourceBuilder(NativeDisplaySource.Edid);
        foreach (var path in paths.Where(static path => path.Edid is not null))
        {
            var fact = CreateIdentityFact(
                builder,
                path,
                payloads.AddEvidence(StrictUtf8.GetBytes($"edid:{path.MonitorDevicePath}:{path.TargetId}")),
                observedAt,
                includeFriendlyName: false);
            var edid = path.Edid!;
            if (!string.IsNullOrWhiteSpace(edid.ProductName))
            {
                fact.FriendlyNameTextIndex = builder.AddText(
                    NativeDisplayTextKind.FriendlyName,
                    edid.ProductName);
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.FriendlyName;
            }
            if (!string.IsNullOrWhiteSpace(edid.SerialNumber))
            {
                fact.EdidSerialTextIndex = builder.AddText(
                    NativeDisplayTextKind.IdentityToken,
                    edid.SerialNumber);
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.EdidSerial;
                fact.IdentityMask |= (ulong)NativeDisplayIdentityMask.EdidSerial;
            }
            if (edid.BitsPerColorChannel is > 0)
            {
                fact.BitsPerColorChannel = edid.BitsPerColorChannel.Value;
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.BitsPerColorChannel;
            }
            builder.Add(fact);
        }
        return builder.Build(NativeDisplaySourceStatus.Complete);
    }

    private static NativeDisplaySourceBatch CreateOemProfiles(
        IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> profiles,
        NativeDisplayCoordinatorPayloadCatalog payloads,
        long observedAt)
    {
        var builder = new SourceBuilder(NativeDisplaySource.OemConnectorProfile);
        foreach (var profile in profiles)
        {
            var fact = new NativeDisplayFact
            {
                StructSize = SizeOf<NativeDisplayFact>(),
                SourceId = (uint)NativeDisplaySource.OemConnectorProfile,
                ValidMask = (ulong)(
                    NativeDisplayFactValidity.SourceObjectKey
                    | NativeDisplayFactValidity.PayloadHandle
                    | NativeDisplayFactValidity.OutputTechnology
                    | NativeDisplayFactValidity.OemMatchRule
                    | NativeDisplayFactValidity.FriendlyName
                    | NativeDisplayFactValidity.ObservedAtUtc),
                IdentityMask = (ulong)NativeDisplayIdentityMask.SourceObjectKey,
                SourceRecordOrdinal = builder.NextOrdinal,
                OutputTechnology = checked((uint)profile.OutputTechnology),
                FriendlyNameTextIndex = builder.AddText(
                    NativeDisplayTextKind.FriendlyName,
                    profile.DisplayName),
                SourceObjectKey = StableKey(profile.Id),
                PayloadHandle = payloads.AddOemProfile(profile),
                ObservedAtUtcMilliseconds = observedAt,
                MatchFlags = profile.AllowAnyActiveAdapter
                    ? (uint)NativeDisplayOemMatchFlags.AllowAnyActiveAdapter
                    : 0
            };
            if (!string.IsNullOrWhiteSpace(profile.PreferredAdapterHardwareIdToken))
            {
                fact.MatchTextIndex = builder.AddText(
                    NativeDisplayTextKind.AdapterMatchToken,
                    profile.PreferredAdapterHardwareIdToken);
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.PreferredAdapterToken;
            }
            builder.Add(fact);
        }
        return builder.Build(NativeDisplaySourceStatus.Complete);
    }

    private static NativeDisplayFact CreateBaseFact(
        SourceBuilder builder,
        DeviceTopologyDisplayPath path,
        NativeDisplayHandle128 payload,
        ulong adapterLuid,
        long observedAt)
    {
        var fact = CreateIdentityFact(builder, path, payload, observedAt);
        fact.AdapterLuid = adapterLuid;
        fact.TargetId = path.TargetId;
        fact.ValidMask |= (ulong)(
            NativeDisplayFactValidity.AdapterLuid
            | NativeDisplayFactValidity.TargetId
            | NativeDisplayFactValidity.OutputTechnology
            | NativeDisplayFactValidity.StatusFlags);
        fact.IdentityMask |= (ulong)NativeDisplayIdentityMask.Target;
        fact.OutputTechnology = checked((uint)path.OutputTechnology);
        fact.StatusFlags = (uint)(
            (path.Active ? NativeDisplayStatusFlags.Active : 0)
            | (path.TargetAvailable ? NativeDisplayStatusFlags.Connected : 0)
            | (WindowsDisplayPathTopologyReader.IsInternalOutput(path.OutputTechnology)
                ? NativeDisplayStatusFlags.Internal
                : 0));
        if (path.ConnectorInstance > 0)
        {
            fact.ConnectorInstance = path.ConnectorInstance;
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.ConnectorInstance;
        }
        if (path.PositionX is { } x
            && path.PositionY is { } y
            && path.Width is > 0
            && path.Height is > 0)
        {
            fact.PositionX = x;
            fact.PositionY = y;
            fact.Width = checked((int)path.Width.Value);
            fact.Height = checked((int)path.Height.Value);
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.Bounds;
        }
        if (path.RefreshRateNumerator > 0 && path.RefreshRateDenominator > 0)
        {
            fact.RefreshNumerator = path.RefreshRateNumerator;
            fact.RefreshDenominator = path.RefreshRateDenominator;
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.RefreshRate;
        }
        if (path.AdvancedColor is { } color)
        {
            fact.CapabilityFlags = (ulong)(
                (color.AdvancedColorSupported
                    ? NativeDisplayCapabilityFlags.AdvancedColorSupported
                    : 0)
                | (color.AdvancedColorEnabled
                    ? NativeDisplayCapabilityFlags.AdvancedColorEnabled
                    : 0)
                | (color.HighDynamicRangeSupported
                    ? NativeDisplayCapabilityFlags.HighDynamicRangeSupported
                    : 0)
                | (color.HighDynamicRangeUserEnabled
                    ? NativeDisplayCapabilityFlags.HighDynamicRangeUserEnabled
                    : 0)
                | (color.HighDynamicRangeActive
                    ? NativeDisplayCapabilityFlags.HighDynamicRangeActive
                    : 0));
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.CapabilityFlags;
            if (color.BitsPerColorChannel is > 0)
            {
                fact.BitsPerColorChannel = color.BitsPerColorChannel.Value;
                fact.ValidMask |= (ulong)NativeDisplayFactValidity.BitsPerColorChannel;
            }
        }
        return fact;
    }

    private static NativeDisplayFact CreateIdentityFact(
        SourceBuilder builder,
        DeviceTopologyDisplayPath path,
        NativeDisplayHandle128 payload,
        long observedAt,
        bool includeFriendlyName = true)
    {
        var adapterLuid = CreateTargetIdentity(path);
        var fact = new NativeDisplayFact
        {
            StructSize = SizeOf<NativeDisplayFact>(),
            SourceId = (uint)builder.Source,
            ValidMask = (ulong)(
                NativeDisplayFactValidity.AdapterLuid
                | NativeDisplayFactValidity.TargetId
                | NativeDisplayFactValidity.PayloadHandle
                | NativeDisplayFactValidity.ObservedAtUtc),
            IdentityMask = (ulong)NativeDisplayIdentityMask.Target,
            SourceRecordOrdinal = builder.NextOrdinal,
            AdapterLuid = adapterLuid,
            TargetId = path.TargetId,
            ObservedAtUtcMilliseconds = observedAt,
            PayloadHandle = payload
        };
        if (!string.IsNullOrWhiteSpace(path.MonitorDevicePath))
        {
            fact.MonitorPathTextIndex = builder.AddText(
                NativeDisplayTextKind.MonitorDevicePath,
                path.MonitorDevicePath);
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.MonitorPath;
            fact.IdentityMask |= (ulong)NativeDisplayIdentityMask.MonitorPath;
        }
        if (!string.IsNullOrWhiteSpace(path.SourceDeviceName))
        {
            fact.SourceDeviceTextIndex = builder.AddText(
                NativeDisplayTextKind.SourceDeviceName,
                path.SourceDeviceName);
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.SourceDeviceName;
            fact.IdentityMask |= (ulong)NativeDisplayIdentityMask.SourceDeviceName;
        }
        if (includeFriendlyName
            && !string.IsNullOrWhiteSpace(path.MonitorFriendlyName))
        {
            fact.FriendlyNameTextIndex = builder.AddText(
                NativeDisplayTextKind.FriendlyName,
                path.MonitorFriendlyName);
            fact.ValidMask |= (ulong)NativeDisplayFactValidity.FriendlyName;
        }
        return fact;
    }

    private static NativeDisplaySourceBatch Empty(
        NativeDisplaySource source,
        NativeDisplaySourceStatus status)
        => new(source, status, [], [], []);

    private static ulong CreateTargetIdentity(DeviceTopologyDisplayPath path)
    {
        var result = ((ulong)(uint)path.AdapterHighPart << 32) | path.AdapterLowPart;
        return result != 0
            ? result
            : throw new InvalidDataException(
                "A DisplayConfig target reported an invalid zero adapter LUID.");
    }

    private static ulong StableKey(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        var result = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        return result == 0 ? 1UL : result;
    }

    private static void SetLuminance(
        ref NativeDisplayFact fact,
        double? minimum,
        double? maximum,
        double? fullFrame)
    {
        var minimumValue = ToMilliNits(minimum, allowZero: true);
        var maximumValue = ToMilliNits(maximum, allowZero: false);
        var fullFrameValue = ToMilliNits(fullFrame, allowZero: false);
        if (minimumValue is null
            || maximumValue is null
            || fullFrameValue is null
            || minimumValue > fullFrameValue
            || fullFrameValue > maximumValue)
        {
            return;
        }
        fact.MinimumLuminanceMilliNits = minimumValue.Value;
        fact.MaximumLuminanceMilliNits = maximumValue.Value;
        fact.MaximumFullFrameLuminanceMilliNits = fullFrameValue.Value;
        fact.ValidMask |= (ulong)(
            NativeDisplayFactValidity.MinimumLuminance
            | NativeDisplayFactValidity.MaximumLuminance
            | NativeDisplayFactValidity.FullFrameLuminance);
    }

    internal static uint? ToMilliNits(double? value, bool allowZero)
    {
        if (value is not { } nits
            || !double.IsFinite(nits)
            || (allowZero ? nits < 0 : nits <= 0))
        {
            return null;
        }
        var result = Math.Round(nits * 1000d, MidpointRounding.AwayFromZero);
        return result is >= 0 and <= uint.MaxValue ? checked((uint)result) : null;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private sealed class SourceBuilder(NativeDisplaySource source)
    {
        private readonly ArrayBufferWriter<byte> textBytes = new();
        private readonly List<NativeDisplayFact> facts = [];
        private readonly List<NativeDisplayTextInput> texts = [];

        internal NativeDisplaySource Source { get; } = source;

        internal ulong NextOrdinal => checked((ulong)facts.Count + 1);

        internal uint AddText(NativeDisplayTextKind kind, string value)
        {
            var normalized = value.TrimEnd('\0').Trim();
            var byteCount = StrictUtf8.GetByteCount(normalized);
            if (byteCount == 0)
            {
                throw new InvalidDataException("Display text facts must not be empty.");
            }
            var offset = textBytes.WrittenCount;
            var destination = textBytes.GetSpan(byteCount);
            var written = StrictUtf8.GetBytes(normalized, destination);
            textBytes.Advance(written);
            texts.Add(new NativeDisplayTextInput
            {
                StructSize = SizeOf<NativeDisplayTextInput>(),
                NormalizationKind = (uint)kind,
                ByteOffset = checked((uint)offset),
                ByteLength = checked((uint)written),
                ValidMask = (ulong)NativeDisplayTextValidity.Required
            });
            return checked((uint)texts.Count - 1);
        }

        internal void Add(NativeDisplayFact fact) => facts.Add(fact);

        internal NativeDisplaySourceBatch Build(NativeDisplaySourceStatus status)
            => new(Source, status, facts.ToArray(), texts.ToArray(), textBytes.WrittenSpan.ToArray());
    }
}
