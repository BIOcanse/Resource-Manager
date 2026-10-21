using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace ResourceManager.Uninstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var text = ToolText.For(AppLanguage.System);
        try
        {
            var plan = UninstallationPlan.Read();
            text = ToolText.FromInstallRoot(plan.Root);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException(text.UserUnknown);
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (args.Length == 4 && args[0] == "--prepare" && args[2] == "--caller-sid")
            {
                if (!elevated || args[3] != sid || args[1] is not ("keep" or "delete"))
                    throw new UnauthorizedAccessException(text.ElevationIdentity);
                var source = Path.Combine(plan.ManagerDirectory, UninstallationPlan.ExecutableName);
                var temporary = Path.Combine(Path.GetTempPath(), "ResourceManager-uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                var helper = Path.Combine(temporary, UninstallationPlan.ExecutableName);
                File.Copy(source, helper, overwrite: false);
                using (var original = File.OpenRead(source))
                using (var copy = File.OpenRead(helper))
                    if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copy))) throw new IOException("Uninstaller copy verification failed.");
                ScheduleHelperCleanup(helper, temporary);
                _ = Process.Start(new ProcessStartInfo(helper)
                {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = temporary, ArgumentList = { "--perform", args[1], "--caller-sid", sid }
                }) ?? throw new InvalidOperationException(text.UninstallHelperFailed);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--perform" && args[2] == "--caller-sid")
            {
                if (!elevated || args[3] != sid || args[1] is not ("keep" or "delete"))
                    throw new UnauthorizedAccessException(text.ElevationIdentity);
                var archive = UninstallationWorkflow.Run(plan, args[1] == "keep", new WindowsUninstallationRuntime());
                if (archive is not null)
                    _ = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { plan.Root } });
                MessageBox.Show(archive is null ? text.UninstallCompleted : text.Format(text.UninstallArchiveCompletedFormat, archive),
                    text.UninstallerTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException(text.InvalidCommand);
            using var form = new Form { Text = text.UninstallerTitle, Width = 610, Height = 310, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var explanation = new Label { Text = text.Format(text.UninstallActionsFormat, plan.Root), Dock = DockStyle.Top, Height = 150, Padding = new Padding(16) };
            var keep = new CheckBox { Text = text.UninstallKeepData, Checked = true, Dock = DockStyle.Top, Height = 36, Padding = new Padding(16, 0, 0, 0) };
            var action = new Button { Text = text.UninstallKeepAction, Width = 220, Height = 36, Left = 16, Top = 205 };
            keep.CheckedChanged += (_, _) => action.Text = keep.Checked ? text.UninstallKeepAction : text.UninstallDeleteAction;
            action.Click += (_, _) =>
            {
                try
                {
                    if (!keep.Checked && MessageBox.Show(text.UninstallDeleteWarning, text.UninstallerTitle, MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    var executable = Path.Combine(plan.ManagerDirectory, UninstallationPlan.ExecutableName);
                    _ = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = true, Verb = elevated ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = plan.ManagerDirectory, ArgumentList = { "--prepare", keep.Checked ? "keep" : "delete", "--caller-sid", sid }
                    }) ?? throw new InvalidOperationException(text.UninstallHelperFailed);
                    form.Close(); // This executable must exit before the helper removes the installation.
                }
                catch (Exception error) { MessageBox.Show(error.Message, text.UninstallFailed, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            form.Controls.AddRange([action, keep, explanation]);
            Application.Run(form);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, text.UninstallFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static void ScheduleHelperCleanup(string helper, string directory)
    {
        // Only the unique temporary copy created above; no installed or user data paths.
        if (!MoveFileExW(helper, null, 4) || !MoveFileExW(directory, null, 4))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string? destination, uint flags);

}
