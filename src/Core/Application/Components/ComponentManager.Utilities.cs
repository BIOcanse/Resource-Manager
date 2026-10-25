using ResourceManager.App.Domain.Components;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager
{
    private static bool IsBundledComponent(ComponentDefinition definition)
    {
        return definition.IsBundled;
    }

    private static void EnsureOptionalDependency(string id)
    {
        if (!ComponentCatalog.IsOptionalDependency(id))
        {
            throw new InvalidOperationException("该组件没有受控下载安装动作，只能验证或打开来源。");
        }
    }
}
