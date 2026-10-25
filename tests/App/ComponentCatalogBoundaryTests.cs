using System.Text.Json;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class ComponentCatalogBoundaryTests
{
    [Fact]
    public void HttpCatalogsSerializeBusinessFactsWithoutPresentationFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        IReadOnlyList<ComponentStatus> components = ComponentCatalog.Definitions.Select(definition =>
            new ComponentStatus(definition, ComponentStatusStates.Missing, "", "", null,
                false, false, false, false, false, true,
                [new ComponentProviderStatus(definition.Id, definition.Vendor, ComponentProviderKinds.Provider,
                    ComponentProviderStates.Missing, BackendMessage.Create(BackendMessageDomains.Dependency,
                        BackendMessageCodes.Dependency.ProviderBridgeMissing), null,
                    definition.Capabilities.Select(capability => capability.Id).ToArray())],
                BackendMessage.Create(BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.ProviderBridgeMissing))).ToArray();
        using var componentJson = JsonDocument.Parse(JsonSerializer.Serialize(components, options));
        foreach (var component in componentJson.RootElement.EnumerateArray())
        {
            Assert.False(component.TryGetProperty("stateLabel", out _));
            var definition = component.GetProperty("definition");
            foreach (var field in new[] { "name", "category", "purpose", "installNote" })
                Assert.False(definition.TryGetProperty(field, out _));
            Assert.NotEmpty(definition.GetProperty("id").GetString()!);
            foreach (var capability in definition.GetProperty("capabilities").EnumerateArray())
            {
                Assert.False(capability.TryGetProperty("label", out _));
                Assert.NotEmpty(capability.GetProperty("providerId").GetString()!);
            }
            var provider = component.GetProperty("providers")[0];
            Assert.False(provider.TryGetProperty("name", out _));
            Assert.Equal(JsonValueKind.Object, provider.GetProperty("message").ValueKind);
        }

        IReadOnlyList<OptionalDependencyStatus> dependencies = OptionalDependencyCatalog.Definitions.Select(definition =>
            new OptionalDependencyStatus(definition, "missing", "", "", "", null,
                false, false, false, false, "", null, "none",
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.ManualAcquisition)))
            .ToArray();
        using var dependencyJson = JsonDocument.Parse(JsonSerializer.Serialize(dependencies, options));
        foreach (var dependency in dependencyJson.RootElement.EnumerateArray())
        {
            var definition = dependency.GetProperty("definition");
            foreach (var field in new[] { "name", "category", "installNote" })
                Assert.False(definition.TryGetProperty(field, out _));
            Assert.NotEmpty(definition.GetProperty("installedProductNames").EnumerateArray());
        }
    }

    [Fact]
    public void VersionAvailabilityTransportsAReasonCodeWithoutProse()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var versions = new DependencyVersionOptions("component", DependencyInstallerSourceKinds.GitHubRelease,
            [new DependencyVersionOption(DependencyVersionChoices.Latest, false, null, null, "network")]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(versions, options));
        var option = json.RootElement.GetProperty("options")[0];
        Assert.False(option.TryGetProperty("unavailableReason", out _));
        Assert.Equal("network", option.GetProperty("unavailableReasonCode").GetString());
    }
}
