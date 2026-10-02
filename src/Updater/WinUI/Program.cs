using ResourceManager.Updater;
using ResourceManager.Shared.Localization;

namespace ResourceManager.UpdateManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            XamlGeneratedProgram.XamlGeneratedMain();
            return 0;
        }
        var text = ToolText.For(AppLanguage.System);
        try
        {
            try { text = ToolText.FromInstallRoot(UpdateManagerCommand.GetInstalledRoot(text)); }
            catch (InvalidOperationException) { }
            var message = UpdateManagerCommand.ExecuteAsync(args,
                Environment.ProcessPath ?? throw new InvalidOperationException(text.ManagerPathUnknown), text: text)
                .GetAwaiter().GetResult();
            UpdateManagerCommand.WriteResult(args, true, message);
            return 0;
        }
        catch (Exception exception)
        {
            UpdateManagerCommand.WriteResult(args, false, exception.ToString());
            return 1;
        }
    }
}
