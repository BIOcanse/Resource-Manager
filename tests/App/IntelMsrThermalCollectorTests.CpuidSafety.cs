using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Theory]
    [InlineData("signature")]
    [InlineData("vendor")]
    [InlineData("capability")]
    public void WarmReaderRejectsChangedCpuUnderTheSameLogicalAddressBeforeOpeningMsr(string change)
    {
        var device = Core();
        var access = new Access();
        var hardware = new Hardware(access);
        var reader = new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access);
        var requested = Request(device, MetricFieldCode.IntelCoreDtsDistance);

        Assert.Single(Assert.Single(reader.Read([device], requested)).Values);
        Assert.Equal(1, hardware.Opens);
        Assert.Single(hardware.Reads);
        var firstCurrentCalls = access.TotalCurrentCalls;

        // The catalogue and address are deliberately unchanged. They cannot be
        // used as evidence that the actual selected processor is still the same.
        if (change == "signature") access.Signature++;
        else if (change == "vendor") access.Vendor = "AuthenticAMD";
        else access.ThermalFlags = 0;

        var changed = Assert.Single(reader.Read([device], requested));
        Assert.True(changed.RequiresTopologyRefresh);
        Assert.Empty(changed.Values);
        Assert.Equal(1, hardware.Opens);
        Assert.Single(hardware.Reads);
        Assert.Equal(2, access.SelectionCalls);
        Assert.True(access.TotalCurrentCalls > firstCurrentCalls);
        Assert.Equal(2, access.CpuidCaptures);
        Assert.Equal(0, hardware.Writes);
    }
}
