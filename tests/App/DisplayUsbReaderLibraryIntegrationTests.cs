using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayUsbReaderLibraryIntegrationTests
{
    [Fact]
    public void SharedDisplayFieldsKeepDeclarationsAndActiveCurrentValuesDistinct()
    {
        var display = new DeviceTopologyDisplayInformation("display", true, true, EdidRawDeclaredBase64: "AA==",
            CurrentWidth: 1920, CurrentHeight: 1080, CurrentRefreshRateNumerator: 60000, CurrentRefreshRateDenominator: 1001,
            ClockwiseRotationDegrees: 0, HdrSupportedReported: false, HdrUserEnabledActual: false,
            HdrActiveActual: false, ColorModeActual: 0, CurrentBitsPerColorChannel: 10,
            AdvancedColorSupported: false, AdvancedColorEnabled: false, SdrWhiteLevelNits: 80,
            ColorEncoding: "RGB", MinimumLuminanceNits: 0);
        var active = WindowsInformationFieldProjection.DisplayFields(display);
        Assert.Equal("0", Field(active, DeviceInformationFieldRegistry.DisplayAllRotationActual).Value!.Value.GetString());
        Assert.False(Field(active, DeviceInformationFieldRegistry.DisplayAllHdrEnabledActual).Value!.Value.GetBoolean());
        Assert.Equal(1001U, Field(active, DeviceInformationFieldRegistry.DisplayAllRefreshRateActual).Value!.Value.GetProperty("denominator").GetUInt32());
        Assert.Equal(0d, Field(active, DeviceInformationFieldRegistry.DisplayAllMinimumLuminanceReportedNits).Value!.Value.GetDouble());
        var inactive = WindowsInformationFieldProjection.DisplayFields(display with { Active = false });
        Assert.Null(Field(inactive, DeviceInformationFieldRegistry.DisplayAllRotationActual).Value);
        Assert.Null(Field(inactive, DeviceInformationFieldRegistry.DisplayAllHdrEnabledActual).Value);
        Assert.False(Field(inactive, DeviceInformationFieldRegistry.DisplayAllHdrCapability).Value!.Value.GetBoolean());
        Assert.Equal("AA==", Field(inactive, DeviceInformationFieldRegistry.DisplayAllEdidRawDeclared).Value!.Value.GetString());
        var unavailable = WindowsInformationFieldProjection.DisplayFields(display with { TargetAvailable = false });
        Assert.Null(Field(unavailable, DeviceInformationFieldRegistry.DisplayAllRotationActual).Value);
        Assert.Null(Field(unavailable, DeviceInformationFieldRegistry.DisplayAllResolutionActual).Value);
        Assert.False(Field(unavailable, DeviceInformationFieldRegistry.DisplayAllHdrCapability).Value!.Value.GetBoolean());
        Assert.All(Assert.Single(WindowsInformationFieldProjection.Displays([display], null).Devices).Fields,
            field => Assert.Null(field.Value));
    }

    [Fact]
    public void RawUsbProjectionUsesUniqueDirectParentAndRetainsUnknownPollingFields()
    {
        var port = UsbParentPortInformationCollectorTests.Raw() with {
            Descriptor = UsbParentPortInformationCollectorTests.Raw().Descriptor with {
                HidDescriptors = [new(0, 0, 0, 0, 0x0111, 0, null)] } };
        var device = new DeviceTopologyNativeDevice(@"USB\DEVICE", null, null, null, null,
            UsbParentPortInformationCollectorTests.DriverKey, null, null, null, null, [], [], [], null, null, null);
        var at = DateTimeOffset.UtcNow;
        var capture = new DeviceTopologyUsbPortSnapshot([port], WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port]), null);
        var fields = Assert.Single(WindowsUsbHubIoctlReader.ProjectFields(capture, [device], at).Devices).Fields;
        Assert.Equal(UsbParentPortInformationCollectorTests.Parent,
            Field(fields, DeviceInformationFieldRegistry.UsbAllParentPortActual).Value!.Value.GetString());
        Assert.Null(Field(fields, DeviceInformationFieldRegistry.HidAllPollIntervalTheoretical).Value);
        Assert.Null(Field(fields, DeviceInformationFieldRegistry.HidAllReportRateTheoreticalHz).Value);
        var ambiguous = new DeviceTopologyUsbPortSnapshot([port, port with { PortNumber = 8 }],
            WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port, port with { PortNumber = 8 }]), null);
        Assert.Empty(WindowsUsbHubIoctlReader.ProjectFields(ambiguous, [device], at).Devices);
        Assert.Empty(WindowsUsbHubIoctlReader.ProjectFields(capture, [device with { DriverKey = "different" }], at).Devices);
        Assert.All(Assert.Single(WindowsUsbHubIoctlReader.ProjectFields(capture, [device], null).Devices).Fields,
            field => Assert.Null(field.Value));
    }

    [Fact]
    public void HardwareFactsAndEndpointFormulaRemainIndependentWhileHostKeepsMessages()
    {
        var library = typeof(ReadWriteCore).Assembly;
        Assert.All(new[] { typeof(WindowsDisplayPathTopologyReader), typeof(WindowsMonitorEdidReader),
            typeof(WindowsDxgiDisplayCapabilityReader), typeof(EdidParser), typeof(WindowsUsbHubIoctlReader),
            typeof(UsbDescriptorParser), typeof(UsbEndpointTiming) }, type => Assert.Equal(library, type.Assembly));
        var unknown = WindowsDeviceTopologyReader.DescribeUsbConnectionStatus(uint.MaxValue);
        Assert.Equal(BackendMessageCodes.DeviceTopology.UsbUnknownStatus, unknown.Code);
        Assert.Equal("4294967295", Assert.Single(unknown.Args));
        Assert.Equal(BackendMessageCodes.DeviceTopology.UsbConnected, WindowsDeviceTopologyReader.DescribeUsbConnectionStatus(1).Code);
        Assert.Equal(125d, UsbEndpointTiming.CalculateServiceIntervalMicroseconds(2, 3, 1));
        Assert.Null(UsbEndpointTiming.CalculateServiceIntervalMicroseconds(2, 3, 17));
        Assert.Null(UsbEndpointTiming.CalculateServiceIntervalMicroseconds(2, 2, 1));
    }

    private static DeviceInformationField Field(IReadOnlyList<DeviceInformationField> fields, DeviceInformationDefinition definition)
        => Assert.Single(fields, field => field.Definition == definition);
}
