using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ResourceManager.Updater;
using ResourceManager.Shared.Localization;
using Windows.Storage.Pickers;

namespace ResourceManager.UpdateManager;

public sealed partial class MainWindow : Window
{
    private ToolText text = ToolText.For(AppLanguage.System);
    private string? installRoot;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        ApplyCopy();
        InstallStatus.Text = text.ReadingInstallation;
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

    private void ApplyCopy()
    {
        Title = text.ManagerTitle;
        HeadingText.Text = text.Heading;
        IntroText.Text = text.Intro;
        RecoverButton.Content = text.Recover;
        PackageHeadingText.Text = text.PackageHeading;
        PackagePath.PlaceholderText = text.PackagePlaceholder;
        BrowseButton.Content = text.Browse;
        VersionRulesText.Text = text.VersionRules;
        UpdateButton.Content = text.Update;
        RepairButton.Content = text.Repair;
    }

    private void RefreshInstallation()
    {
        try
        {
            installRoot = UpdateManagerCommand.GetInstalledRoot(text);
            text = ToolText.FromInstallRoot(installRoot);
            ApplyCopy();
            InstallStatus.Text = text.Format(text.InstallStatusFormat, installRoot);
            var managerFailure = UpdateManagerCommand.GetManagerUpdateFailure(installRoot);
            ManagerStatus.Text = managerFailure is null ? "" : text.Format(text.ManagerFailureFormat, managerFailure);
            var pending = UpdateManagerCommand.GetPendingRecoveryDescriptions(installRoot);
            RecoveryStatus.Text = pending.Count == 0
                ? text.NoRecovery
                : text.Format(text.RecoveryFoundFormat, string.Join("; ", pending));
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
            var summary = await Task.Run(() => UpdateManagerCommand.DescribePackageAction(packageRoot, target, repair, text));
            var actions = summary + "\n\n" + text.PackageActions;
            if (!await ConfirmAsync(repair ? text.Repair : text.Update, actions)) return;
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
        var actions = text.RecoveryActions;
        SetBusy(true);
        try
        {
            if (!await ConfirmAsync(text.Recover, actions)) return;
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
            PrimaryButtonText = text.Continue,
            CloseButtonText = text.Cancel,
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowCompletionAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = text.Completed,
            Content = new TextBlock
            {
                Text = message + "\n\n" + text.SelfReplaceNotice,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = text.CloseManager
        };
        await dialog.ShowAsync();
    }

    private async Task<string> RunCommandAsync(string[] args)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(text.ManagerPathUnknown);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return await Task.Run(() => UpdateManagerCommand.ExecuteAsync(args, executable, text: text));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)
            ?? throw new InvalidOperationException(text.ManagerChildFailed);
        await child.WaitForExitAsync();
        if (child.ExitCode != 0)
            throw new InvalidOperationException(text.OperationFailed);
        return text.OperationSucceeded;
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
