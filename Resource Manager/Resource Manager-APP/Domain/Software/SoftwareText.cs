namespace ResourceManager.App.Domain.Software;

public static class SoftwareText
{
    public const string Adapted = "适配软件";
    public const string Controlled = "受控软件";
    public const string Managed = "受管软件";
    public const string Game = "游戏";
    public const string HighPerformance = "高性能软件";
    public const string General = "一般应用";
    public const string WindowsSystem = "Windows 系统";
    public const string WindowsApp = "Windows 应用";
    public const string WindowsComponent = "Windows 应用/组件";
    public const string WindowsService = "Windows 服务";
    public const string Unattributed = "未归属进程";

    public static string DisplayKind(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Adapted => Adapted,
            SoftwareKinds.Controlled => Controlled,
            SoftwareKinds.Managed => Managed,
            SoftwareKinds.Game => Game,
            SoftwareKinds.HighPerformance => HighPerformance,
            SoftwareKinds.WindowsSystem => WindowsSystem,
            SoftwareKinds.WindowsComponent => WindowsComponent,
            SoftwareKinds.WindowsService => WindowsService,
            SoftwareKinds.RuntimePackage => WindowsApp,
            SoftwareKinds.RuntimeProduct => General,
            SoftwareKinds.RuntimeRoot => General,
            SoftwareKinds.Unattributed => Unattributed,
            _ => General
        };
    }
}
