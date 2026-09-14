using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var serviceOnly = args.Contains("--service-action", StringComparer.OrdinalIgnoreCase);
        try
        {
            var restart = args.Contains("--restart", StringComparer.OrdinalIgnoreCase);
            var background = args.Contains("--background-startup", StringComparer.OrdinalIgnoreCase);
            if (args.Any(arg => arg is not ("--restart" or "--background-startup" or "--service-action")))
                throw new ArgumentException("Unknown launcher argument.");
            using var identity = WindowsIdentity.GetCurrent();
            var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var installation = DesktopInstallation.Read(registry);
            var state = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
            if (state is not null) WindowsServiceRegistration.RequireExpectedBinary(state, installation.Backend);
            if (serviceOnly)
            {
                if (!admin) throw new UnauthorizedAccessException("Service registration requires administrator permission.");
                WindowsServiceRegistration.EnsureRunning(WindowsServiceRegistration.ProductServiceName,
                    installation.Backend, restart);
                return 0;
            }
            if (admin)
                throw new InvalidOperationException("Open the launcher normally, not with Run as administrator. Only its service action is elevated.");
            if (state is null || !state.Running || restart)
            {
                var start = new ProcessStartInfo(installation.Launcher)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(installation.Launcher)!
                };
                start.ArgumentList.Add("--service-action");
                if (restart) start.ArgumentList.Add("--restart");
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Service action did not start.");
                if (!child.WaitForExit(100_000)) throw new TimeoutException("Service action did not finish.");
                if (child.ExitCode != 0) throw new InvalidOperationException($"Service action failed (exit {child.ExitCode}).");
            }
            using var frontend = Process.Start(new ProcessStartInfo(installation.NativeUi)
            {
                UseShellExecute = true,
                Arguments = background ? "--background-startup" : "--show-existing",
                WorkingDirectory = Path.GetDirectoryName(installation.NativeUi)!
            }) ?? throw new InvalidOperationException("The frontend did not start.");
            return 0;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (serviceOnly) Console.Error.WriteLine(exception.Message);
            else MessageBox.Show(exception.Message, "Resource Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
