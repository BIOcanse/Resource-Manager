using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView : ISoftwareRegistryView
{
    private readonly IAdapterSoftwareRegistry adapterRegistry;
    private readonly IControlledSoftwareRegistry controlledRegistry;
    private readonly IOptionalDependencyManager dependencyManager;
    private readonly IManualSoftwareRegistry manualSoftwareRegistry;
    private readonly ISoftwarePolicyProfileProvider policyProfileProvider;
    private readonly IInstalledSoftwareInventory installedSoftwareInventory;
    private readonly ISoftwareIdentityCatalog softwareIdentityCatalog;
    private readonly IPortableSoftwareRegistry portableSoftwareRegistry;
    private readonly IManagementInventoryRevisionOwner? inventoryRevisionOwner;

    public SoftwareRegistryView(
        IAdapterSoftwareRegistry adapterRegistry,
        IControlledSoftwareRegistry controlledRegistry,
        IOptionalDependencyManager dependencyManager,
        IManualSoftwareRegistry manualSoftwareRegistry,
        ISoftwarePolicyProfileProvider policyProfileProvider,
        IInstalledSoftwareInventory installedSoftwareInventory,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        IPortableSoftwareRegistry portableSoftwareRegistry,
        IManagementInventoryRevisionOwner? inventoryRevisionOwner = null)
    {
        this.adapterRegistry = adapterRegistry;
        this.controlledRegistry = controlledRegistry;
        this.dependencyManager = dependencyManager;
        this.manualSoftwareRegistry = manualSoftwareRegistry;
        this.policyProfileProvider = policyProfileProvider;
        this.installedSoftwareInventory = installedSoftwareInventory;
        this.softwareIdentityCatalog = softwareIdentityCatalog;
        this.portableSoftwareRegistry = portableSoftwareRegistry;
        this.inventoryRevisionOwner = inventoryRevisionOwner;
    }

    public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: false, windowsSid: null, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(string? windowsSid, CancellationToken cancellationToken,
        IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: false, windowsSid: windowsSid, cancellationToken: cancellationToken,
            runtimeSoftware: runtimeSoftware);
    }

    public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: true, windowsSid: null, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(string? windowsSid, CancellationToken cancellationToken,
        IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
    {
        return GetSoftwareCoreAsync(refreshPortableRegistrations: true, windowsSid: windowsSid, cancellationToken: cancellationToken,
            runtimeSoftware: runtimeSoftware);
    }

    private async Task<IReadOnlyList<SoftwareRecord>> GetSoftwareCoreAsync(
        bool refreshPortableRegistrations,
        string? windowsSid,
        CancellationToken cancellationToken,
        IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
    {
        var records = new List<SoftwareRecord>();
        var policyProfile = await policyProfileProvider.GetProfileAsync(cancellationToken);

        records.AddRange(await GetAdaptedSoftwareAsync(cancellationToken));
        records.AddRange(GetControlledRegistrationRecords());

        var dependencies = await dependencyManager.GetStatusesAsync(windowsSid, cancellationToken);
        records.AddRange(dependencies.Select(ToDependencyRecord));


        var reservedSoftware = records.ToArray();
        var installedSoftware = refreshPortableRegistrations
            ? await installedSoftwareInventory.RefreshInstalledSoftwareAsync(windowsSid, cancellationToken)
            : await installedSoftwareInventory.GetInstalledSoftwareAsync(windowsSid, cancellationToken);
        records.AddRange(installedSoftware
            .Where(item => !HasKnownSoftwareMatch(item, reservedSoftware))
            .Select(ToInstalledSoftwareRecord));

        var portableSoftware = refreshPortableRegistrations
            ? await portableSoftwareRegistry.RefreshAsync(cancellationToken)
            : portableSoftwareRegistry.GetSnapshot();
        records.AddRange(portableSoftware.Select(ToPortableSoftwareRecord));

        ApplyPolicyProfile(records, policyProfile);
        var revisionRecords = records.ToList();

        IncludeRuntimeSoftware(records, runtimeSoftware ?? []);

        var manualRecords = await manualSoftwareRegistry.GetAllAsync(cancellationToken);
        ApplyManualRecords(records, manualRecords);
        ApplyManualRecords(revisionRecords, manualRecords);

        var revisionContent = revisionRecords
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => MergeRecords(group.ToArray()))
            .OrderBy(static record => KindSort(record.Kind))
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = records
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => MergeRecords(group.ToArray()))
            .OrderBy(static record => KindSort(record.Kind))
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        inventoryRevisionOwner?.ObserveSoftwareContent(windowsSid, revisionContent);
        return result;
    }
}
