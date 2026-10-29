using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace ResourceManager.Launcher;

internal static class Program
{
    private static readonly string[] AllowedArguments = ["--restart", "--background-startup", "--service-action"];
    [STAThread]
    private static int Main(string[] args)
    {
        var serviceOnly = args.Contains("--service-action", StringComparer.OrdinalIgnoreCase);
        var resultIndex = Array.IndexOf(args, "--service-result");
        var resultPipe = resultIndex >= 0 && resultIndex + 1 < args.Length ? args[resultIndex + 1] : null;
        var text = ToolText.For(AppLanguage.System);
        try
        {
            var restart = args.Contains("--restart", StringComparer.OrdinalIgnoreCase);
            var background = args.Contains("--background-startup", StringComparer.OrdinalIgnoreCase);
            using var identity = WindowsIdentity.GetCurrent();
            var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var registration = registry.OpenSubKey(@"Software\ResourceManager");
            text = ToolText.FromInstallRoot(registration?.GetValue("InstallRoot") as string);
            var ordinaryArguments = resultIndex < 0 ? args : args.Where((_, index) => index != resultIndex && index != resultIndex + 1).ToArray();
            if ((resultIndex >= 0 && (!serviceOnly || resultPipe is null))
                || ordinaryArguments.Any(arg => !AllowedArguments.Contains(arg, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException(text.LauncherInvalidArguments);
            if (!serviceOnly && registration?.GetValue("InstallRoot") is string root
                && UpdateManagerCommand.GetPendingRecoveryDescriptions(root).Count > 0)
            {
                if (MessageBox.Show(text.LaunchRecovery, "Resource Manager", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    _ = Process.Start(new ProcessStartInfo(UpdateManagerPaths.InstalledExecutable(root)) { UseShellExecute = true });
                return 1;
            }
            var installation = DesktopInstallation.Read(registry, text);
            using var packageUse = PackageUseGuard.Acquire(installation.Root);
            var state = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
            if (state is not null) WindowsServiceRegistration.RequireExpectedBinary(state, installation.Backend);
            if (serviceOnly)
            {
                if (!admin) throw new UnauthorizedAccessException(text.LauncherServicePermission);
                WindowsServiceRegistration.StartExisting(WindowsServiceRegistration.ProductServiceName,
                    installation.Backend, restart);
                RequireRunningSystemService(installation.Backend, text);
                if (resultPipe is not null) ServiceActionResult.Write(resultPipe, "");
                return 0;
            }
            LauncherWorkflow.Start(admin, restart, installation.Backend,
                () => WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName),
                () => WindowsServiceRegistration.StartExisting(WindowsServiceRegistration.ProductServiceName, installation.Backend, restart),
                () => ServiceActionResult.RunElevatedAsync(installation.Launcher, restart, text).GetAwaiter().GetResult(),
                () =>
                {
                    // requireAdministrator: unelevated, ShellExecute asks for UAC consent; elevated, it inherits this token.
                    using var frontend = Process.Start(new ProcessStartInfo(installation.NativeUi)
                    {
                        UseShellExecute = true,
                        Arguments = background ? "--background-startup" : "--show-existing",
                        WorkingDirectory = Path.GetDirectoryName(installation.NativeUi)!
                    }) ?? throw new InvalidOperationException(text.LauncherUiStartFailed);
                }, text.LauncherServiceStartFailed);
            return 0;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (serviceOnly && resultPipe is not null)
            {
                try { ServiceActionResult.Write(resultPipe, exception.Message); }
                catch (Exception reportFailure) { Trace.WriteLine(reportFailure); }
            }
            else if (serviceOnly) Console.Error.WriteLine(exception.Message);
            else MessageBox.Show(text.Format(text.LauncherFailedFormat, exception.Message),
                "Resource Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void RequireRunningSystemService(string backend, ToolText text)
    {
        var state = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new InvalidOperationException(text.LauncherServiceStartFailed);
        WindowsServiceRegistration.RequireExpectedBinary(state, backend);
        if (!state.Running) throw new InvalidOperationException(text.LauncherServiceStartFailed);
    }
}
