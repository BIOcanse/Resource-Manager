using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.SoftwareDiscovery;

namespace ResourceManager.App.Application.SoftwareDiscovery;

public static class PortableSoftwareObservationFactory
{
    private const string CatalogPrefix = "catalog:";

    public static PortableSoftwareObservation? TryCreate(
        RuntimeProcessIdentity process,
        RuntimeSoftwareAttribution exactAttribution,
        bool identityConfirmed = false)
    {
        if (!exactAttribution.Id.StartsWith(CatalogPrefix, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return null;
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(process.ExecutablePath.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var rootPath = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        return new PortableSoftwareObservation(
            exactAttribution.Id,
            exactAttribution.Id[CatalogPrefix.Length..],
            exactAttribution.Name,
            exactAttribution.Kind,
            executablePath,
            rootPath,
            identityConfirmed,
            RootPathConfirmed: false);
    }
}
