using Microsoft.Extensions.Logging;
using ResourceManager.NativeUi.StateItems;
using ResourceManager.NativeUi.SystemIntegration;

namespace ResourceManager.NativeUi;

/// <summary>
/// The window process. It runs only as the child of the resident <c>ResourceManager.Desktop.exe</c>, which starts it
/// with the private pipe; started any other way it hands its command to the resident and exits
/// (<see cref="ResidentHandoff"/>).
/// </summary>
internal static class Program
{
    private const string AppUserModelId = "ResourceManager.Desktop";

    [STAThread]
    private static int Main(string[] args)
    {
        new NativeUiTraceLogger<NativeWindowApplicationContext>().LogInformation("State registry version {RegistryVersion}.", StateRegistry.Version);
        using var packageUse = new NativeUiPackageUseLifetime();
        using var residentPipe = ResidentWindowPipe.FromArguments(args);
        if (residentPipe is null) return HandOffToResident(NativeUiLaunchOptions.ParseCommand(args));
        // 界面语言的来源是持久化设置里的 appearance.language（含 "system" 取值）；
        // 前端起来后每次切换都会通过 shell.language 消息把解析后的语言送过来。
        Localization.NativeUiText.ApplyPersisted();
        NativeAppIdentity.TrySetCurrentProcessAppUserModelId(AppUserModelId);
        ApplicationConfiguration.Initialize();
        // Upgrade step: an earlier version's Run value becomes this user's logon task (see the adapter).
        LegacyLoginStartupRunValue.MigrateCurrentUser();
        using var windowContext = new NativeWindowApplicationContext(residentPipe,
            new NativeUiApplicationServices { ApiBaseAddress = ResidentWindowPipe.ApiBaseFromArguments(args) });
        Application.Run(windowContext);
        return 0;
    }

    private static int HandOffToResident(string command)
    {
        var text = ResourceManager.Shared.Localization.ToolText.FromInstallRoot(
            ResourceManager.Shared.Packages.ProductDesktopLayout.PackageRootFromUiDirectory(AppContext.BaseDirectory));
        return ResidentHandoffRunner.Run(() => ResidentHandoff.ForCurrentProcess().Run(command), text,
            message => MessageBox.Show(message, "Resource Manager", MessageBoxButtons.OK, MessageBoxIcon.Error));
    }

    private static class NativeAppIdentity
    {
        public static void TrySetCurrentProcessAppUserModelId(string appUserModelId)
        {
            try
            {
                _ = SetCurrentProcessExplicitAppUserModelID(appUserModelId);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
    }
}
