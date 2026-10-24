using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class ComponentManagementRoleTests
{
    [Fact]
    public void FanControlIsAvailableWithoutInstallingAComponent()
    {
        Assert.DoesNotContain(ComponentCatalog.Definitions, row => row.Id == "fan-control-core");
        Assert.Null(ResourceManager.App.Application.Dependencies.OptionalDependencyCatalog.Find("fan-control-core"));
        Assert.Equal("FanControlCore.Library", typeof(FanControlCore.Discovery.FanCore).Assembly.GetName().Name);
    }

    [Fact]
    public void CatalogProviderKindsUseCanonicalClassificationTokens()
    {
        var expected = new Dictionary<string, string>
        {
            ["shared-webview2-runtime"] = ComponentProviderKinds.Runtime,
            ["amd-smu-pawnio-provider"] = ComponentProviderKinds.Cpu,
            ["intel-msr-pawnio-provider"] = ComponentProviderKinds.Cpu,
            ["hardware-bridge"] = ComponentProviderKinds.Cpu,
            ["hardware-sensors-pawnio-provider"] = ComponentProviderKinds.HardwareMonitor,
            ["librehardwaremonitor-provider"] = ComponentProviderKinds.HardwareMonitor,
            ["notebook-oem-fan-provider"] = ComponentProviderKinds.NotebookOem,
            ["nvidia-nvml-provider"] = ComponentProviderKinds.Gpu,
            ["nvidia-nvapi-provider"] = ComponentProviderKinds.Gpu,
            ["amd-adlx-provider"] = ComponentProviderKinds.Gpu
        };
        Assert.Equal(expected.Count, ComponentCatalog.Definitions.Count);
        foreach (var definition in ComponentCatalog.Definitions)
        {
            Assert.NotEmpty(definition.Capabilities);
            Assert.All(definition.Capabilities, capability =>
                Assert.Equal(expected[definition.Id], capability.ProviderKind));
        }
        var tokens = typeof(ComponentProviderKinds).GetFields()
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue())).ToArray();
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token => Assert.Matches("^[a-z][a-z0-9_]*$", token));
    }

    [Fact]
    public void CurrentComponentsAreDependenciesAndSupportRemainsAvailableForExtensions()
    {
        Assert.NotEmpty(ComponentCatalog.Definitions);
        Assert.All(
            ComponentCatalog.Definitions,
            definition =>
            {
                Assert.True(SoftwareManagementRoles.IsKnown(
                    definition.ManagementRole));
                Assert.Equal(
                    SoftwareManagementRoles.Dependency,
                    definition.ManagementRole);
            });
        Assert.DoesNotContain(
            ComponentCatalog.Definitions,
            definition => definition.ManagementRole
                == SoftwareManagementRoles.Support);
    }

    [Fact]
    public void DependencyAndSupportAreManagementRolesNotSoftwareKinds()
    {
        var softwareKinds = typeof(SoftwareKinds)
            .GetFields(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly)
            .Select(static field => Assert.IsType<string>(
                field.GetRawConstantValue()))
            .ToArray();

        Assert.DoesNotContain(SoftwareManagementRoles.Dependency, softwareKinds);
        Assert.DoesNotContain(SoftwareManagementRoles.Support, softwareKinds);
    }
}
