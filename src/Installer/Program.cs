using System.Diagnostics;
using System.Security.Principal;

namespace ResourceManager.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.Contains("--plan-only", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (quiet)
            {
                if (args.Length != 1) throw new ArgumentException("Invalid plan-only arguments.");
                _ = PackageLayout.Verify(AppContext.BaseDirectory);
                return 0;
            }
            var confirmed = args.Length == 4 && args[0] == "--confirmed" && args[2] == "--caller-sid";
            if (!confirmed && args.Length != 0) throw new ArgumentException("Unknown installer argument.");
            var source = PackageLayout.Verify(confirmed ? args[1] : AppContext.BaseDirectory);
            var target = InstallerWorkflow.TargetFor(source);
            InstallationRegistry.RequireVacant(target);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户。");
            var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (confirmed)
            {
                if (!admin || sid != args[3]) throw new UnauthorizedAccessException("提权使用了不同的 Windows 用户或未取得管理员权限。");
            }
            else
            {
                var actions = $"即将安装 Resource Manager 到：\n{target.Root}\n\n" +
                    "本次安装将执行：\n" +
                    "1. 复制并校验发行程序：提供后台服务和桌面界面。\n" +
                    "2. 注册 HKLM 产品路径及 App Paths：让启动入口定位程序，并支持系统查找。\n" +
                    "3. 向 Windows 服务管理器注册手动启动的 LocalSystem 服务：供后台监测使用；安装时不会启动。\n" +
                    "4. 在主程序目录外安装独立更新管理器并登记 App Paths：供升级、中断恢复和主程序修复使用。\n" +
                    "5. 建立主程序与更新管理器的所有用户开始菜单快捷方式：方便系统搜索和手动固定到任务栏。\n\n" +
                    "此入口不会启动产品，也不会升级已有版本。是否同意并继续？";
                if (MessageBox.Show(actions, "安装 Resource Manager", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return 2;
                if (!admin)
                {
                    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = source.Root,
                        ArgumentList = { "--confirmed", source.Root, "--caller-sid", sid }
                    }) ?? throw new InvalidOperationException("管理员安装进程未启动。");
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            InstallerWorkflow.Install(source, target);
            MessageBox.Show($"安装完成。\n\n开始菜单中可搜索 Resource Manager；也可运行 {target.Start}。",
                "Resource Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            if (!quiet) MessageBox.Show(error.Message, "Resource Manager 安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
