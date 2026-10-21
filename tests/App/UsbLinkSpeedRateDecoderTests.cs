using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class UsbLinkSpeedRateDecoderTests
{
    private const uint Connected = 1;
    private const uint PortNumber = 7;

    [Theory]
    [InlineData(0, null, 1.5d)]
    [InlineData(1, null, 12d)]
    [InlineData(2, null, null)]
    [InlineData(3, null, null)]
    [InlineData(0, false, 1.5d)]
    [InlineData(1, false, 12d)]
    [InlineData(2, false, 480d)]
    [InlineData(3, false, null)]
    [InlineData(2, true, 5000d)]
    [InlineData(3, true, 5000d)]
    [InlineData(0, true, null)]
    [InlineData(1, true, null)]
    public void KnownRatesRequireEvidenceForTheCurrentTier(byte legacySpeed, bool? superSpeed, double? expected)
    {
        var capability = superSpeed is null ? null : Capability(superSpeed.Value, superSpeedPlus: false);
        var actual = UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected,
            legacySpeed,
            capability,
            null,
            PortNumber);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DisconnectedAndFailedHighSpeedV2QueriesRemainUnavailable()
    {
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            0, 2, Capability(superSpeed: false, superSpeedPlus: false), null, PortNumber));
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, null, null, PortNumber));
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 3, null, null, PortNumber));
    }

    [Fact]
    public void SuperSpeedPlusRequiresConsistentFlagsAndSamePortLaneEvidence()
    {
        var plus = Capability(superSpeed: true, superSpeedPlus: true);
        var lanes = LaneInfo(PortNumber, 0, 0);

        Assert.Equal<double?>(10_000d, UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, plus, lanes, PortNumber));
        Assert.Equal<double?>(20_000d, UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, plus, LaneInfo(PortNumber, 1, 1), PortNumber));
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, plus, LaneInfo(PortNumber + 1, 0, 0), PortNumber));
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, Capability(superSpeed: false, superSpeedPlus: true), lanes, PortNumber));
        Assert.Null(UsbLinkSpeedRateDecoder.NegotiatedMegabitsPerSecond(
            Connected, 2, plus, null, PortNumber));
    }

    [Fact]
    public void AsymmetricOrMalformedLaneAttributesRemainUnavailable()
    {
        var asymmetric = LaneInfo(PortNumber, 0, 0) with
        {
            RxRateAttribute = LaneAttribute(transmit: false, mode: 1),
            TxRateAttribute = LaneAttribute(transmit: true, mode: 1)
        };
        var unequal = LaneInfo(PortNumber, 0, 1);
        var wrongDirection = LaneInfo(PortNumber, 0, 0) with
        {
            RxRateAttribute = LaneAttribute(transmit: true)
        };
        var reservedBits = LaneInfo(PortNumber, 0, 0) with
        {
            RxRateAttribute = LaneAttribute(transmit: false) | (1u << 8)
        };
        var unsupportedProtocol = LaneInfo(PortNumber, 0, 0) with
        {
            RxRateAttribute = LaneAttribute(transmit: false) | (2u << 14)
        };
        var zeroMantissa = LaneInfo(PortNumber, 0, 0) with
        {
            RxRateAttribute = LaneAttribute(transmit: false) & 0x0000FFFF
        };
        var tooManyLanes = LaneInfo(PortNumber, 2, 0);
        var wrongLength = LaneInfo(PortNumber, 0, 0) with { Length = 20 };

        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(asymmetric));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(unequal));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(wrongDirection));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(reservedBits));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(unsupportedProtocol));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(zeroMantissa));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(tooManyLanes));
        Assert.Null(UsbLinkSpeedRateDecoder.DecodeSymmetricScalarRate(wrongLength));
    }

    private static DeviceTopologyUsbPortCapability Capability(bool superSpeed, bool superSpeedPlus)
        => new(false, false, true, superSpeed, false, superSpeedPlus, false);

    private static UsbSuperSpeedPlusLaneInformation LaneInfo(
        uint port,
        uint rxLaneCountZeroBased,
        uint txLaneCountZeroBased)
        => new(port, 24,
            LaneAttribute(transmit: false), rxLaneCountZeroBased,
            LaneAttribute(transmit: true), txLaneCountZeroBased);

    private static uint LaneAttribute(bool transmit, uint mode = 0)
        => (10u << 16) | (1u << 14) | (mode << 6) | (transmit ? 1u << 7 : 0) | (3u << 4);
}
