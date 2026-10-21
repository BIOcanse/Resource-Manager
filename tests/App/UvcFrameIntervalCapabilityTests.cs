using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class UvcFrameIntervalCapabilityTests
{
    internal const string FieldName = "camera_all_frame_interval_capability";
    internal const string Pnp = @"USB\DEVICE";
    internal const string Identity = "windows-pnp|" + Pnp;

    [Fact]
    public void DiscreteIntervalsKeepEveryIntegerDefaultAndExactDirectoryIdentity()
    {
        var bytes = Configuration(Interface(3, 2), Format(4, 1), Frame(1, 666_667, [333_667, 666_667, uint.MaxValue]));
        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(bytes));
        var intervals = Assert.IsType<DeviceTopologyUsbCameraFrameIntervals>(mode.FrameIntervalsDeclared);
        Assert.Equal((byte)1, intervals.ConfigurationValue);
        Assert.Equal((byte)3, intervals.InterfaceNumber);
        Assert.Equal((byte)2, intervals.AlternateSetting);
        Assert.Equal((byte)4, intervals.FormatIndex);
        Assert.Equal((byte)1, intervals.FrameIndex);
        Assert.Equal(666_667U, intervals.DefaultFrameInterval100Nanoseconds);
        Assert.Equal<uint>([333_667, 666_667, uint.MaxValue], intervals.DiscreteIntervals100Nanoseconds!);
        Assert.Null(intervals.ContinuousInterval);
        Assert.Equal(10_000_000d / 333_667, mode.MaximumFrameRate);
        Array.Clear(bytes);
        Assert.Equal<uint>([333_667, 666_667, uint.MaxValue], intervals.DiscreteIntervals100Nanoseconds!);

        var field = IntervalField(WindowsInformationFieldProjection.UsbDevices(
            [new(Identity, "HUB|7", null, null, null, CameraModes: [mode])], DateTimeOffset.UtcNow));
        Assert.Equal("100ns", field.Definition.Unit);
        Assert.Equal("usb.video-class-descriptors", field.Definition.SourceId);
        var row = field.Value!.Value[0];
        Assert.Equal(Identity, row.GetProperty("usbDeviceIdentity").GetString());
        var value = row.GetProperty("frameIntervalsDeclared");
        Assert.Equal(uint.MaxValue, value.GetProperty("discreteIntervals100Nanoseconds")[2].GetUInt32());
        Assert.Equal(666_667U, value.GetProperty("defaultFrameInterval100Nanoseconds").GetUInt32());
        var publicReading = InformationFieldProjection.Read(Identity, field, DateTimeOffset.UtcNow);
        Assert.Equal(FieldObservationStatus.Current, publicReading.Status);
        Assert.Equal("structured", InformationFieldProjection.Describe(field.Definition).ValueKind);
        Assert.Equal(field.Value.Value.GetRawText(), publicReading.Value!.Structure!.Value.GetRawText());
    }

    [Fact]
    public void DiscreteDeclarationKeepsOriginalOrderAndMultiplicityWhileMaximumFpsUsesWholeList()
    {
        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(0, 0), Format(1, 1), Frame(1, 666_666, [666_666, 333_333, 666_666]))));
        Assert.Equal<uint>([666_666, 333_333, 666_666], mode.FrameIntervalsDeclared!.DiscreteIntervals100Nanoseconds!);
        Assert.Equal(10_000_000d / 333_333, mode.MaximumFrameRate);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(333_333U)]
    public void ContinuousKeepsRawMinMaxStepAndDefaultWithoutExpanding(uint step)
    {
        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(0, 0), Format(1, 1), ContinuousFrame(1, 666_666, 333_333, 999_999, step))));
        var intervals = mode.FrameIntervalsDeclared!;
        Assert.Null(intervals.DiscreteIntervals100Nanoseconds);
        Assert.Equal(new DeviceTopologyUsbCameraContinuousFrameInterval(333_333, 999_999, step), intervals.ContinuousInterval);
        Assert.Equal(666_666U, intervals.DefaultFrameInterval100Nanoseconds);
        Assert.Equal(10_000_000d / 333_333, mode.MaximumFrameRate);
    }

    [Fact]
    public void EqualDimensionsAndNamesDoNotMergeFormatInterfaceAlternateOrFrameIndices()
    {
        var modes = UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(1, 0), Format(1, 2), Frame(1, 333_333, [333_333]), Frame(2, 333_333, [333_333]),
            Format(2, 1), Frame(1, 333_333, [333_333]),
            Interface(1, 1), Format(1, 1), Frame(1, 333_333, [333_333]),
            Interface(2, 0), Format(1, 1), Frame(1, 333_333, [333_333])));
        Assert.Equal(5, modes.Count);
        Assert.All(modes, mode => Assert.Equal("YUY2", mode.PixelFormat));
        Assert.Equal(5, modes.Select(mode => (mode.FrameIntervalsDeclared!.InterfaceNumber,
            mode.FrameIntervalsDeclared.AlternateSetting, mode.FrameIntervalsDeclared.FormatIndex,
            mode.FrameIntervalsDeclared.FrameIndex)).Distinct().Count());
        Assert.Equal(5, IntervalField(WindowsInformationFieldProjection.UsbDevices(
            [new(Identity, null, null, null, null, CameraModes: modes)], DateTimeOffset.UtcNow)).Value!.Value.GetArrayLength());
    }

    [Fact]
    public void MjpegNeedsItsOwnMatchingFormatAndUnknownPayloadDoesNotReuseOffsets()
    {
        var unsupportedFormat = new byte[] { 4, 0x24, 0x10, 1 };
        var unsupportedFrame = Frame(1, 333_333, [333_333]);
        unsupportedFrame[2] = 0x11;
        unsupportedFrame[25] = 255; // An unrelated payload layout must not be decoded as an interval count.
        var modes = UsbDescriptorParser.ParseUvcCameraModes(Configuration(Interface(0, 0),
            unsupportedFormat, unsupportedFrame, Format(2, 1, mjpeg: true),
            Frame(1, 333_333, [333_333], mjpeg: true)));
        Assert.Equal("MJPEG", Assert.Single(modes).PixelFormat);
        Assert.Equal((byte)2, modes[0].FrameIntervalsDeclared!.FormatIndex);
        Assert.Empty(UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(0, 0), Format(1, 1, mjpeg: true), Frame(1, 333_333, [333_333]))));
    }

    [Theory]
    [InlineData("configuration-truncated")]
    [InlineData("truncated-discrete")]
    [InlineData("extra-frame-bytes")]
    [InlineData("zero-interval")]
    [InlineData("default-not-supported")]
    [InlineData("default-zero")]
    [InlineData("zero-width")]
    [InlineData("missing-frame")]
    [InlineData("duplicate-frame")]
    [InlineData("duplicate-format")]
    [InlineData("late-bad-framing")]
    [InlineData("maximum-count-truncated")]
    public void InvalidSupportedDirectoryNeverPublishesAValidPrefix(string fault)
    {
        var frame = Frame(1, 333_333, [333_333, 666_666]);
        var format = Format(1, 1);
        byte[][] entries = [Interface(0, 0), format, frame];
        switch (fault)
        {
            case "truncated-discrete": frame[25] = 3; break;
            case "extra-frame-bytes": frame = [.. frame, 0, 0, 0, 0]; frame[0] = (byte)frame.Length; entries[2] = frame; break;
            case "zero-interval": BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(26), 0); break;
            case "default-not-supported": BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(21), 500_000); break;
            case "default-zero": BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(21), 0); break;
            case "zero-width": frame[5] = frame[6] = 0; break;
            case "missing-frame": format[4] = 2; break;
            case "duplicate-frame": format[4] = 2; entries = [Interface(0, 0), format, frame, frame]; break;
            case "duplicate-format": entries = [Interface(0, 0), format, frame, format, frame]; break;
            case "late-bad-framing": entries = [Interface(0, 0), format, frame, [0]]; break;
            case "maximum-count-truncated": frame[25] = byte.MaxValue; break;
        }
        var bytes = Configuration(entries);
        if (fault == "configuration-truncated") bytes = bytes[..^1];
        Assert.Empty(UsbDescriptorParser.ParseUvcCameraModes(bytes));
    }

    [Theory]
    [InlineData(0U, 1_000_000U, 0U, 500_000U)]
    [InlineData(999_999U, 333_333U, 0U, 666_666U)]
    [InlineData(333_333U, 999_999U, 0U, 1_000_000U)]
    public void InvalidContinuousDeclarationIsUnknown(uint minimum, uint maximum, uint step, uint preferred)
        => Assert.Empty(UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(0, 0), Format(1, 1), ContinuousFrame(1, preferred, minimum, maximum, step))));

    [Theory]
    [InlineData(333_333U, 999_999U, 333_333U, 500_000U)] // default is not on an assumed min+n*step grid.
    [InlineData(333_333U, 1_000_000U, 333_333U, 666_666U)] // max is not on that assumed grid.
    public void RawContinuousDeclarationDoesNotInventDefaultOrMaximumGridConstraints(
        uint minimum, uint maximum, uint step, uint preferred)
    {
        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(Configuration(
            Interface(0, 0), Format(1, 1), ContinuousFrame(1, preferred, minimum, maximum, step))));
        var field = IntervalField(WindowsInformationFieldProjection.UsbDevices(
            [new(Identity, null, null, null, null, CameraModes: [mode])], DateTimeOffset.UtcNow));
        var evidence = field.Value!.Value[0].GetProperty("frameIntervalsDeclared");
        Assert.Equal(preferred, evidence.GetProperty("defaultFrameInterval100Nanoseconds").GetUInt32());
        var range = evidence.GetProperty("continuousInterval");
        Assert.Equal(minimum, range.GetProperty("minimum100Nanoseconds").GetUInt32());
        Assert.Equal(maximum, range.GetProperty("maximum100Nanoseconds").GetUInt32());
        Assert.Equal(step, range.GetProperty("step100Nanoseconds").GetUInt32());
    }

    [Fact]
    public void LegacyConstructorAndDeconstructionKeepMaximumFpsButCannotInventIntervals()
    {
        var mode = new DeviceTopologyUsbCameraModeDescriptor(1920, 1080, 29.97, "YUY2");
        var (width, height, fps, format) = mode;
        Assert.Equal((1920U, 1080U, 29.97, "YUY2"), (width, height, fps, format));
        var fields = WindowsInformationFieldProjection.UsbFields(new(Identity, null, null, null, null, CameraModes: [mode]));
        Assert.NotNull(Assert.Single(fields, field => field.Definition == DeviceInformationFieldRegistry.CameraAllUvcModeCapability).Value);
        Assert.Null(Assert.Single(fields, field => field.Definition == DeviceInformationFieldRegistry.CameraAllFrameIntervalCapability).Value);
    }

    [Fact]
    public void CallerInvalidOrLegacyRowCannotBecomeAPartialCompleteIntervalArray()
    {
        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(RecordedConfiguration()));
        foreach (var other in new[] { mode, new(1920, 1080, 30, "legacy"),
            mode with { FrameIntervalsDeclared = mode.FrameIntervalsDeclared! with { DefaultFrameInterval100Nanoseconds = 0 } },
            mode with { Width = 0 } })
        {
            var fields = WindowsInformationFieldProjection.UsbFields(new(Identity, null, null, null, null, CameraModes: [mode, other]));
            Assert.Null(Assert.Single(fields, field => field.Definition == DeviceInformationFieldRegistry.CameraAllFrameIntervalCapability).Value);
        }
    }

    [Fact]
    public void PublicHubAndCoreRoundTripPreserveTheSameExactIntegerEvidence()
    {
        var port = RawPort();
        var native = new DeviceTopologyNativeDevice(Pnp, null, null, null, null,
            UsbParentPortInformationCollectorTests.DriverKey, null, null, null, null, [], [], [], null, null, null);
        var capture = new DeviceTopologyUsbPortSnapshot([port], WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port]), null);
        var at = DateTimeOffset.UtcNow;
        var direct = IntervalField(WindowsUsbHubIoctlReader.ProjectFields(capture, [native], at));
        var product = IntervalField(DeviceTopologyInformationProjection.Create(Snapshot(), at));
        Assert.Equal(direct.Value!.Value.GetRawText(), product.Value!.Value.GetRawText());
        Assert.Empty(WindowsUsbHubIoctlReader.ProjectFields(capture, [native with { DriverKey = "foreign" }], at).Devices);
        var ambiguousPort = port with { PortNumber = 8 };
        var ambiguous = new DeviceTopologyUsbPortSnapshot([port, ambiguousPort],
            WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port, ambiguousPort]), null);
        Assert.Empty(WindowsUsbHubIoctlReader.ProjectFields(ambiguous, [native], at).Devices);
        Assert.Null(IntervalField(DeviceTopologyInformationProjection.Create(Snapshot(), null)).Value);
    }

    [Fact]
    public void IntegerOnlyCapabilityChangeChangesOriginalSemanticHashAndSurvivesSnapshotJson()
    {
        var original = Snapshot();
        var camera = original.Ports[0].Camera!;
        var first = camera.NativeModes[0];
        var changed = first with { FrameIntervalsDeclared = first.FrameIntervalsDeclared! with {
            DiscreteIntervals100Nanoseconds = new uint[] { 333_667, 666_667, 999_999 } } };
        var updated = original with { Ports = [original.Ports[0] with {
            Camera = camera with { BestMode = changed, NativeModes = [changed] } }] };
        Assert.Equal(first.MaximumFrameRate, changed.MaximumFrameRate);
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(original), comparer.ComputeHash(updated));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(updated, options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DeviceTopologySnapshot>(json, options)!;
        Assert.Equal<uint>([333_667, 666_667, 999_999], restored.Ports[0].Camera!.NativeModes[0]
            .FrameIntervalsDeclared!.DiscreteIntervals100Nanoseconds!);
        Assert.Null(IntervalField(DeviceTopologyInformationProjection.Create(restored, null)).Value);
    }

    internal static DeviceInformationField IntervalField(DeviceInformationSnapshot frame)
        => DeviceInformationTestData.Field(frame, Identity, FieldName);
    internal static DeviceTopologyUsbPort RawPort()
    {
        var original = UsbParentPortInformationCollectorTests.Raw();
        return original with { Descriptor = original.Descriptor with {
            CameraModes = UsbDescriptorParser.ParseUvcCameraModes(RecordedConfiguration()) } };
    }
    internal static DeviceTopologySnapshot Snapshot()
    {
        var raw = RawPort();
        var port = DeviceInformationTestData.Port(Pnp) with { BusKind = DeviceBusKinds.Usb,
            Usb = WindowsDeviceTopologyReader.CreateUsbConnection(raw, true),
            Camera = DeviceTopologySpecializedCapabilityProjector.CreateCamera(raw) };
        return DeviceInformationTestData.Snapshot(port);
    }
    internal static byte[] RecordedConfiguration()
        => Configuration(Interface(2, 1), Format(3, 1), Frame(1, 666_667, [333_667, 666_667]));
    internal static byte[] Configuration(params byte[][] descriptors)
    {
        var size = checked(9 + descriptors.Sum(static value => value.Length));
        var bytes = new byte[size];
        new byte[] { 9, 2, (byte)size, (byte)(size >> 8),
            checked((byte)descriptors.Where(static value => value.Length >= 9 && value[1] == 4)
                .Select(static value => value[2]).Distinct().Count()), 1, 0, 0x80, 50 }.CopyTo(bytes, 0);
        var offset = 9;
        foreach (var descriptor in descriptors) { descriptor.CopyTo(bytes, offset); offset += descriptor.Length; }
        return bytes;
    }
    internal static byte[] Interface(byte number, byte alternate) => [9, 4, number, alternate, 0, 0x0E, 2, 0, 0];
    internal static byte[] Format(byte index, byte frames, bool mjpeg = false)
    {
        var bytes = new byte[mjpeg ? 11 : 27];
        bytes[0] = (byte)bytes.Length; bytes[1] = 0x24; bytes[2] = mjpeg ? (byte)6 : (byte)4;
        bytes[3] = index; bytes[4] = frames; bytes[mjpeg ? 6 : 22] = 1;
        if (!mjpeg) { Encoding.ASCII.GetBytes("YUY2").CopyTo(bytes, 5); bytes[21] = 16; }
        return bytes;
    }
    internal static byte[] Frame(byte index, uint preferred, uint[] intervals, bool mjpeg = false)
    {
        var bytes = BaseFrame(checked(26 + intervals.Length * sizeof(uint)), index, preferred, mjpeg);
        bytes[25] = checked((byte)intervals.Length);
        for (var i = 0; i < intervals.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26 + i * 4), intervals[i]);
        return bytes;
    }
    internal static byte[] ContinuousFrame(byte index, uint preferred, uint minimum, uint maximum, uint step)
    {
        var bytes = BaseFrame(38, index, preferred, false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), minimum);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), maximum);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34), step);
        return bytes;
    }
    private static byte[] BaseFrame(int size, byte index, uint preferred, bool mjpeg)
    {
        var bytes = new byte[size]; bytes[0] = checked((byte)size); bytes[1] = 0x24;
        bytes[2] = mjpeg ? (byte)7 : (byte)5; bytes[3] = index;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5), 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7), 1080);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), preferred);
        return bytes;
    }
}
