namespace ResourceManager.NativeUi;

internal static class Program
{
    private const string AppUserModelId = "ResourceManager.Desktop";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = NativeUiLaunchOptions.Parse(args);
        NativeAppIdentity.TrySetCurrentProcessAppUserModelId(AppUserModelId);
        ApplicationConfiguration.Initialize();
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

        Application.Run(new ResourceManagerApplicationContext(
            singleInstance,
            options.StartInBackground));
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
