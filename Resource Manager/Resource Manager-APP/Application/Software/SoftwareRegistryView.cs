using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView : ISoftwareRegistryView
{
    private readonly IAdapterSoftwareRegistry adapterRegistry;
    private readonly IControlledSoftwareRegistry controlledRegistry;
    private readonly IOptionalDependencyManager dependencyManager;
    private readonly ISoftwareDataMigrationManager migrationManager;
    private readonly IManualSoftwareRegistry manualSoftwareRegistry;
    private readonly ISoftwarePolicyProfileProvider policyProfileProvider;
    private readonly IInstalledSoftwareInventory installedSoftwareInventory;
    private readonly ISoftwareIdentityCatalog softwareIdentityCatalog;
    private readonly IPortableSoftwareRegistry portableSoftwareRegistry;

    public SoftwareRegistryView(
        IAdapterSoftwareRegistry adapterRegistry,
        IControlledSoftwareRegistry controlledRegistry,
        IOptionalDependencyManager dependencyManager,
        ISoftwareDataMigrationManager migrationManager,
        IManualSoftwareRegistry manualSoftwareRegistry,
        ISoftwarePolicyProfileProvider policyProfileProvider,
        IInstalledSoftwareInventory installedSoftwareInventory,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        IPortableSoftwareRegistry portableSoftwareRegistry)
    {
        this.adapterRegistry = adapterRegistry;
        this.controlledRegistry = controlledRegistry;
        this.dependencyManager = dependencyManager;
        this.migrationManager = migrationManager;
        this.manualSoftwareRegistry = manualSoftwareRegistry;
        this.policyProfileProvider = policyProfileProvider;
        this.installedSoftwareInventory = installedSoftwareInventory;
        this.softwareIdentityCatalog = softwareIdentityCatalog;
        this.portableSoftwareRegistry = portableSoftwareRegistry;
    }

    public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: false, cancellationToken);
    }

    public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: true, cancellationToken);
    }

    private async Task<IReadOnlyList<SoftwareRecord>> GetSoftwareCoreAsync(
        bool refreshPortableRegistrations,
        CancellationToken cancellationToken)
    {
        var records = new List<SoftwareRecord>();
        var policyProfile = await policyProfileProvider.GetProfileAsync(cancellationToken);

        records.AddRange(await GetAdaptedSoftwareAsync(cancellationToken));
        records.AddRange(GetControlledRegistrationRecords());

        var dependencies = await dependencyManager.GetStatusesAsync(cancellationToken);
        records.AddRange(dependencies.Select(ToDependencyRecord));

        var migrationRecords = await migrationManager.GetRecordsAsync(cancellationToken);
        records.AddRange(ToControlledMigrationRecords(migrationRecords));

        var reservedSoftware = records.ToArray();
        var installedSoftware = refreshPortableRegistrations
            ? await installedSoftwareInventory.RefreshInstalledSoftwareAsync(cancellationToken)
            : await installedSoftwareInventory.GetInstalledSoftwareAsync(cancellationToken);
        records.AddRange(installedSoftware
            .Where(item => !HasKnownSoftwareMatch(item, reservedSoftware))
            .Select(ToInstalledSoftwareRecord));

        var portableSoftware = refreshPortableRegistrations
            ? await portableSoftwareRegistry.RefreshAsync(cancellationToken)
            : portableSoftwareRegistry.GetSnapshot();
        records.AddRange(portableSoftware.Select(ToPortableSoftwareRecord));

        ApplyPolicyProfile(records, policyProfile);

        var manualRecords = await manualSoftwareRegistry.GetAllAsync(cancellationToken);
        ApplyManualRecords(records, manualRecords);

        return records
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => MergeRecords(group.ToArray()))
            .OrderBy(static record => KindSort(record.Kind))
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
