using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class ComponentManagementRoleTests
{
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
