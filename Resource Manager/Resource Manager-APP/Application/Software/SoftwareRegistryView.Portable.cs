using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareDiscovery;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private SoftwareRecord ToPortableSoftwareRecord(PortableSoftwareRegistration registration)
    {
        var projectedKind = registration.IdentityConfirmed ? registration.Kind : SoftwareKinds.Other;
        var confirmationMessage = registration.RequiresRootPathConfirmation
            ? "根目录尚未确认，请检查并选择软件根目录。"
            : "便携软件根目录已确认。";
        return new SoftwareRecord(
            registration.SoftwareId,
            registration.Name,
            projectedKind,
            SoftwareText.DisplayKind(projectedKind),
            registration.RequiresRootPathConfirmation ? "RootConfirmationRequired" : "portable",
            [registration.IdentityConfirmed ? "运行时便携软件确认" : "运行时便携软件候选", $"软件身份目录 {softwareIdentityCatalog.Version}"],
            registration.RootPaths,
            $"已定位 {registration.ExecutablePaths.Count} 个便携程序入口。{confirmationMessage}",
            NoUninstall(BackendMessageCodes.Software.PortableNoUninstall),
            null,
            registration.RequiresRootPathConfirmation,
            registration.IdentityConfirmed,
            registration.SuggestedRootPaths,
            registration.ExecutablePaths,
            registration.CatalogEntryId);
    }
}
