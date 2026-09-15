using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private SoftwareRecord ToInstalledSoftwareRecord(InstalledSoftwareEntry entry)
    {
        var record = ToOtherSoftwareRecord(entry);
        var identity = softwareIdentityCatalog.MatchInstalledSoftware(entry);
        if (identity is null)
        {
            return record;
        }

        return record with
        {
            Kind = identity.Kind,
            DisplayKind = SoftwareDisplayKinds.Project(identity.Kind),
            SoftwareIdentityId = identity.Id,
            Sources = record.Sources
                .Append($"软件身份目录 {softwareIdentityCatalog.Version} · {identity.Source}")
                .ToArray()
        };
    }
}
