namespace ResourceManager.App.Domain.Software;

/// <summary>
/// 软件记录对外显示时归到哪一组。取值是 <see cref="SoftwareKinds"/> 里的标识，不是措辞：
/// 用户看到的名字由前端按当前语言给出（`uiText.softwareKind`）。
/// 它和 <c>Kind</c> 是两件事——<c>Kind</c> 是登记时的真实类别，这里是把若干类别合并后的展示分组。
/// </summary>
public static class SoftwareDisplayKinds
{
    public const string Adapted = SoftwareKinds.Adapted;
    public const string Controlled = SoftwareKinds.Controlled;
    public const string Managed = SoftwareKinds.Managed;
    public const string Game = SoftwareKinds.Game;
    public const string HighPerformance = SoftwareKinds.HighPerformance;
    public const string General = SoftwareKinds.Other;
    public const string WindowsSystem = SoftwareKinds.WindowsSystem;
    public const string WindowsApp = SoftwareKinds.RuntimePackage;
    public const string WindowsComponent = SoftwareKinds.WindowsComponent;
    public const string WindowsService = SoftwareKinds.WindowsService;
    public const string Unattributed = SoftwareKinds.Unattributed;

    /// <summary>系统与驱动占用的那部分资源，不属于任何进程。</summary>
    public const string SystemResidual = "SystemResidual";

    public static string Project(string kind)
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
