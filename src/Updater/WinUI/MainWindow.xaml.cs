using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ResourceManager.Updater;
using Windows.Storage.Pickers;

namespace ResourceManager.UpdateManager;

public sealed partial class MainWindow : Window
{
    private string? installRoot;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 590));
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (executableDirectory is not null
            && string.Equals(Path.GetFileName(executableDirectory), "UpdateManager", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(executableDirectory)), "Internal", StringComparison.OrdinalIgnoreCase))
        {
            var packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(executableDirectory));
            if (packageRoot is not null && File.Exists(Path.Combine(packageRoot, "release-manifest.json")))
                PackagePath.Text = packageRoot;
        }
        RefreshInstallation();
    }

    private void RefreshInstallation()
    {
        try
        {
            installRoot = UpdateManagerCommand.GetInstalledRoot();
            InstallStatus.Text = $"主程序位置：{installRoot}";
            var managerFailure = UpdateManagerCommand.GetManagerUpdateFailure(installRoot);
            ManagerStatus.Text = managerFailure is null ? "" : "上次更新管理器自身更新失败：" + managerFailure;
            var pending = UpdateManagerCommand.GetPendingRecoveryDescriptions(installRoot);
            RecoveryStatus.Text = pending.Count == 0
                ? "没有待恢复的中断事务。"
                : "发现中断事务：" + string.Join("；", pending);
            RecoverButton.IsEnabled = !busy && pending.Count > 0;
        }
        catch (Exception exception)
        {
            installRoot = null;
            InstallStatus.Text = exception.Message;
            ManagerStatus.Text = "";
            RecoveryStatus.Text = "";
            RecoverButton.IsEnabled = false;
        }
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        var enabled = !busy && installRoot is not null && !string.IsNullOrWhiteSpace(PackagePath.Text);
        UpdateButton.IsEnabled = enabled;
        RepairButton.IsEnabled = enabled;
    }

    private void PackagePath_TextChanged(object sender, TextChangedEventArgs args)
        => UpdateActionAvailability();

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) PackagePath.Text = folder.Path;
    }

    private async void Update_Click(object sender, RoutedEventArgs args)
        => await RunPackageActionAsync(repair: false);

    private async void Repair_Click(object sender, RoutedEventArgs args)
        => await RunPackageActionAsync(repair: true);

    private async Task RunPackageActionAsync(bool repair)
    {
        if (installRoot is null || busy) return;
        SetBusy(true);
        var completed = false;
        try
        {
            var packageRoot = Path.GetFullPath(PackagePath.Text.Trim());
            var target = installRoot;
            var summary = await Task.Run(() => UpdateManagerCommand.DescribePackageAction(packageRoot, target, repair));
            var actions = summary + "\n\n管理器将等待桌面界面退出，停止产品服务，校验并保留旧目录，" +
                "切换程序文件，启动服务并检查运行状态。失败时尝试恢复旧程序和数据。" +
                "需要管理员权限；请先从托盘退出 Resource Manager。";
            if (!await ConfirmAsync(repair ? "修复主程序" : "更新主程序", actions)) return;
            var command = repair ? "--repair" : "--apply";
            var message = await RunCommandAsync([command, packageRoot, target]);
            await ShowCompletionAsync(message);
            completed = true;
        }
        catch (Exception exception)
        {
            ShowResult(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshInstallation();
        }
        if (completed) Close();
    }

    private async void Recover_Click(object sender, RoutedEventArgs args)
    {
        if (installRoot is null || busy) return;
        var target = installRoot;
        var actions = "管理器将按中断事务记录停止产品服务，把未完成的新版目录保留为失败副本，" +
            "恢复旧程序和原数据，并尝试重新启动原服务。需要管理员权限；请先从托盘退出 Resource Manager。";
        SetBusy(true);
        try
        {
            if (!await ConfirmAsync("恢复中断更新", actions)) return;
            var message = await RunCommandAsync(["--recover", target]);
            ShowResult(message, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowResult(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshInstallation();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowCompletionAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "操作完成",
            Content = new TextBlock
            {
                Text = message + "\n\n如果发行包带有新版更新管理器，关闭此窗口后会完成自身替换。",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "关闭管理器"
        };
        await dialog.ShowAsync();
    }

    private static async Task<string> RunCommandAsync(string[] args)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定更新管理器路径。");
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return await Task.Run(() => UpdateManagerCommand.ExecuteAsync(args, executable));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("管理员更新进程未能启动。");
        await child.WaitForExitAsync();
        if (child.ExitCode != 0)
            throw new InvalidOperationException("操作未完成。请检查更新目录中的事务记录，或重新打开管理器使用恢复功能。");
        return "操作完成。更新管理器已保留原目录备份。";
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Working.IsActive = value;
        RecoverButton.IsEnabled = !value && RecoverButton.IsEnabled;
        UpdateActionAvailability();
    }

    private void ShowResult(string message, InfoBarSeverity severity)
    {
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }
}
