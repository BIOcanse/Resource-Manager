using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class FirmwareProviderPlanCompilerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionCompilerReadsFixedFirmwareOnlyOncePerOwner(bool unavailable)
    {
        var calls = new List<string>();
        var reader = new FirmwareIdentityReader((scope, query) =>
        {
            Assert.Equal(@"root\CIMV2", scope);
            calls.Add(query);
            return unavailable ? new([], "unavailable") : new([new()
            {
                ["Manufacturer"] = "MECHREVO", ["Model"] = "model", ["Product"] = "board", ["SMBIOSBIOSVersion"] = "1"
            }], null);
        });
        var compiler = new FirmwareProviderPlanCompiler(reader);
        Assert.Empty(calls);
        var first = compiler.Compile();
        for (var index = 0; index < 100; index++) Assert.Equal(first, compiler.Compile());
        Assert.Equal(3, calls.Count);
        Assert.Same(first.Identity, reader.Read());
    }

    [Theory]
    [InlineData("MECHREVO", "JIAOLONG")]
    [InlineData("机械革命", "蛟龙")]
    [InlineData("Tongfang", "GMx")]
    [InlineData("Uniwill", "GMx")]
    public void Compile_MapsMechrevoTongfangFirmwareToUwacpi(string manufacturer, string product)
    {
        var plan = FirmwareProviderPlanCompiler.Compile(new FirmwareIdentitySnapshot(
            BiosManufacturer: manufacturer,
            BiosVersion: "1.0",
            SystemManufacturer: manufacturer,
            SystemProductName: product,
            BaseBoardManufacturer: manufacturer,
            BaseBoardProduct: product));

        Assert.Equal(NotebookOemFanProviderKind.MechrevoUwAcpi, plan.NotebookOemFanProvider);
    }

    [Theory]
    [InlineData("LENOVO", "Legion")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG")]
    [InlineData("Dell Inc.", "Alienware")]
    [InlineData("HP", "OMEN")]
    public void Compile_DoesNotMapUnsupportedFirmwareToUwacpi(string manufacturer, string product)
    {
        var plan = FirmwareProviderPlanCompiler.Compile(new FirmwareIdentitySnapshot(
            BiosManufacturer: manufacturer,
            BiosVersion: "1.0",
            SystemManufacturer: manufacturer,
            SystemProductName: product,
            BaseBoardManufacturer: manufacturer,
            BaseBoardProduct: product));

        Assert.Equal(NotebookOemFanProviderKind.None, plan.NotebookOemFanProvider);
    }

    [Fact]
    public void DefaultMonitoringPlan_DoesNotEnableNotebookOemFanSubProvider()
    {
        Assert.Equal(
            NotebookOemFanProviderKind.None,
            CompiledMonitoringPlan.Default.FirmwareProviders.NotebookOemFanProvider);
    }
}
