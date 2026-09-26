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
            "Active" => "已激活",
            "Installed" => "已安装",
            "InstalledUnverified" => "已安装待验证",
            "ReadyToInstall" => "可安装",
            "Available" => "可下载",
            "ManualDownloadRequired" => "需手动下载",
            "ProviderMissing" => "Provider 未接入",
            "Missing" => "未安装",
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
