using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private async Task<IReadOnlyList<SoftwareRecord>> GetAdaptedSoftwareAsync(CancellationToken cancellationToken)
    {
        var records = new List<SoftwareRecord>
        {
            new(RuntimeAttributionIds.ResourceManagerSelf, ResourceManagerSelfDescriptor.DisplayName,
                SoftwareKinds.Adapted, "active", [new(SoftwareSourceKinds.Self)],
                ResourceManagerSelfDescriptor.ResolveRootPaths(), NoUninstall(SoftwareUninstallConstraints.Self), null,
                RegistrationFacts: new(SoftwareSourceKinds.Self))
        };
        var registrations = await adapterRegistry.GetAllAsync(cancellationToken);
        foreach (var registration in registrations)
        {
            records.Add(new SoftwareRecord(registration.Id, registration.DisplayName,
                SoftwareKinds.Adapted, registration.State, [new(SoftwareSourceKinds.Adapter)],
                registration.ProgramRootPaths, NoUninstall(SoftwareUninstallConstraints.MissingStrategy), null,
                RegistrationFacts: new(SoftwareSourceKinds.Adapter, registration.Processes.Count,
                    registration.Services.Count, registration.LastResourceMarkerProbe.State)));
        }
        return records;
    }

    private IEnumerable<SoftwareRecord> GetControlledRegistrationRecords()
    {
        foreach (var registration in controlledRegistry.GetAll())
        {
            var name = registration.Software.Count > 0 ? string.Join(", ", registration.Software) : registration.Command;
            yield return new SoftwareRecord($"controlled-registration:{registration.Id}", name,
                SoftwareKinds.Controlled, registration.Status, [new(SoftwareSourceKinds.Controlled)],
                registration.ProgramRootPaths, NoUninstall(SoftwareUninstallConstraints.MissingStrategy), null,
                RegistrationFacts: new(SoftwareSourceKinds.Controlled, registration.Processes.Count, registration.Services.Count));
        }
    }
}
