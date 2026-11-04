using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Installer;

internal static class InstallationConsent
{
    internal static string Build(ToolText text, PackageLayout target, UninstallationPlan? previous)
    {
        var steps = new List<string>();
        if (previous is not null) steps.AddRange([text.ReinstallStop, text.ReinstallIdentity, text.ReinstallData]);
        steps.AddRange([text.InstallFiles, text.InstallUserDirectories, text.InstallRegistry, text.InstallService, text.InstallManager, text.InstallShortcuts]);
        return text.Format(text.InstallLocationFormat, target.Root) + "\n\n"
            + (previous is null ? "" : text.Format(text.ReinstallPreviousFormat, previous.Root) + "\n\n")
            + text.InstallActions + "\n" + string.Join("\n", steps.Select((step, index) => $"{index + 1}. {step}"))
            + "\n\n" + text.InstallConsent;
    }
}
