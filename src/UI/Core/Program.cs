using Microsoft.Extensions.Logging;
using ResourceManager.NativeUi.StateItems;

namespace ResourceManager.NativeUi;

internal static class Program
{
    private const string AppUserModelId = "ResourceManager.Desktop";

    [STAThread]
    private static int Main(string[] args)
    {
        if (ElevatedProcessActionBroker.TryExecuteBackendRequest(args) is { } backendExitCode) return backendExitCode;
        if (ElevatedProcessActionBroker.TryExecute(args) is { } processExitCode) return processExitCode;
        new NativeUiTraceLogger<ResourceManagerApplicationContext>().LogInformation("State registry version {RegistryVersion}.", StateRegistry.Version);
        using var packageUse = new NativeUiPackageUseLifetime();
        var options = NativeUiLaunchOptions.Parse(args);
        // 界面语言的来源是持久化设置里的 appearance.language（含 "system" 取值）；
        // 前端起来后每次切换都会通过 shell.language 消息把解析后的语言送过来。
        Localization.NativeUiText.ApplyPersisted();
        NativeAppIdentity.TrySetCurrentProcessAppUserModelId(AppUserModelId);
        ApplicationConfiguration.Initialize();
        using var residentPipe = ResidentWindowPipe.FromArguments(args);
        if (residentPipe is not null)
        {
            using var windowContext = new NativeWindowApplicationContext(residentPipe,
                new NativeUiApplicationServices { ApiBaseAddress = ResidentWindowPipe.ApiBaseFromArguments(args) });
            Application.Run(windowContext);
            return 0;
        }
        // Phase A compatibility: retain standalone composition until layout step 6
        // migrates the launcher and persistent entry points to ResourceManager.Desktop.
        var singleInstance = NativeUiSingleInstanceCoordinator.Create();
        var command = options.SingleInstanceCommand;
        if (command == "exit")
        {
            if (!singleInstance.IsPrimary)
            {
                var signaled = singleInstance.SignalRequest(command);
                singleInstance.Dispose();
                return signaled ? 0 : 2;
            }

            singleInstance.Dispose();
            return 0;
        }

        if (!singleInstance.IsPrimary)
        {
            var signaled = singleInstance.SignalRequest(command ?? "show");
            singleInstance.Dispose();
            return signaled ? 0 : 2;
        }

        using var context = new ResourceManagerApplicationContext(
            singleInstance,
            options.StartInBackground);
        Application.Run(context);
        return 0;
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
