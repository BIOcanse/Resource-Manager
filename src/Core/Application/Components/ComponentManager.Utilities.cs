using ResourceManager.App.Domain.Components;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    private static bool IsBundledComponent(ComponentDefinition definition)
    {
        return definition.IsBundled;
    }

    private static string StateLabel(string state)
    {
        return state switch
        {
            ComponentStatusStates.Active => "已激活",
            ComponentStatusStates.Installed => "已安装",
            ComponentStatusStates.InstalledUnverified => "已安装待验证",
            ComponentStatusStates.ReadyToInstall => "可安装",
            ComponentStatusStates.Available => "可下载",
            ComponentStatusStates.ManualDownloadRequired => "需手动下载",
            ComponentStatusStates.ProviderMissing => "Provider 未接入",
            ComponentStatusStates.Missing => "未安装",
            _ => state
        };
    }

    private static void EnsureOptionalDependency(string id)
    {
        if (!ComponentCatalog.IsOptionalDependency(id))
        {
            throw new InvalidOperationException("该组件没有受控下载安装动作，只能验证或打开来源。");
        }
    }
}
