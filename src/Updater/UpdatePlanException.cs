using ResourceManager.Shared.Localization;

namespace ResourceManager.Updater;

internal sealed class UpdatePlanException(string code, string installedVersion, string targetVersion)
    : InvalidOperationException($"{code}: installed={installedVersion}; target={targetVersion}")
{
    public string Code { get; } = code;
    public string[] Parameters { get; } = [installedVersion, targetVersion];

    public string Format(ToolText text) => text.Format(Code == "update.versionNotNewer"
        ? text.UpdateVersionNotNewerFormat : text.RepairVersionMismatchFormat,
        Parameters.Cast<object>().ToArray());
}
