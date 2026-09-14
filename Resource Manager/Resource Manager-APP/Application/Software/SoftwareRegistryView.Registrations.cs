using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private async Task<IReadOnlyList<SoftwareRecord>> GetAdaptedSoftwareAsync(CancellationToken cancellationToken)
    {
        var records = new List<SoftwareRecord>
        {
            new(
                RuntimeAttributionIds.ResourceManagerSelf,
                ResourceManagerSelfDescriptor.DisplayName,
                SoftwareKinds.Adapted,
                SoftwareText.Adapted,
                "active",
                ["内置适配", "本地资源自管"],
                ResourceManagerSelfDescriptor.ResolveRootPaths(),
                "Resource Manager 自身通过内置调度控制参与监控与归因，并使用进程内默认自管器维护可操作资源。",
                NoUninstall("资源管理器自身不能从这里卸载或迁移。"),
                null)
        };

        var registrations = await adapterRegistry.GetAllAsync(cancellationToken);
        foreach (var registration in registrations)
        {
            records.Add(new SoftwareRecord(
                registration.Id,
                registration.DisplayName,
                SoftwareKinds.Adapted,
                SoftwareText.Adapted,
                registration.State,
                ["适配注册", "适配控制端点"],
                registration.ProgramRootPaths,
                $"适配端点 {AdapterEndpointStateText(registration.LastResourceMarkerProbe.State)}；记录 {registration.Processes.Count} 个识别进程，{registration.Services.Count} 个服务。进程只用于归因和评分，实际适配动作只落到工作区/功能区和资源。",
                NoUninstall("适配软件需要通过注册/manifest 声明卸载策略后才能由 Resource Manager 执行。"),
                null));
        }

        return records;
    }

    private static string AdapterEndpointStateText(string state)
    {
        return state switch
        {
            AdapterResourceMarkerStates.Online => "在线",
            AdapterResourceMarkerStates.Unreachable => "不可达",
            AdapterResourceMarkerStates.Unsupported => "不支持",
            _ => state
        };
    }

    private IEnumerable<SoftwareRecord> GetControlledRegistrationRecords()
    {
        foreach (var registration in controlledRegistry.GetAll())
        {
            var name = registration.Software.Count > 0
                ? string.Join(", ", registration.Software)
                : registration.Command;
            yield return new SoftwareRecord(
                $"controlled-registration:{registration.Id}",
                name,
                SoftwareKinds.Controlled,
                SoftwareText.Controlled,
                registration.Status,
                ["L0注册"],
                registration.ProgramRootPaths,
                $"旧受控注册仅用于根目录/进程归因；声明 {registration.Processes.Count} 个进程，{registration.Services.Count} 个服务。",
                NoUninstall("L0 受控注册没有卸载策略。"),
                null);
        }
    }
}
