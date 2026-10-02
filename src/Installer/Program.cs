using System.Diagnostics;
using ResourceManager.Shared.Localization;
using System.Security.Principal;

namespace ResourceManager.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var text = ToolText.For(AppLanguage.System);
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
            var sid = identity.User?.Value ?? throw new InvalidOperationException(text.UserUnknown);
            var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (confirmed)
            {
                if (!admin || sid != args[3]) throw new UnauthorizedAccessException(text.ElevationIdentity);
            }
            else
            {
                var steps = new[] { text.InstallFiles, text.InstallRegistry, text.InstallService, text.InstallManager, text.InstallShortcuts };
                var actions = text.Format(text.InstallLocationFormat, target.Root) + "\n\n" + text.InstallActions + "\n"
                    + string.Join("\n", steps.Select((step, index) => $"{index + 1}. {step}")) + "\n\n" + text.InstallConsent;
                if (MessageBox.Show(actions, text.InstallerTitle, MessageBoxButtons.YesNo,
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
                    }) ?? throw new InvalidOperationException(text.InstallerChildFailed);
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            InstallerWorkflow.Install(source, target);
            MessageBox.Show(text.Format(text.InstallCompletedFormat, target.Start),
                "Resource Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            if (!quiet) MessageBox.Show(error.Message, text.InstallerFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
