using ResourceManager.Updater;

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
        try
        {
            var message = UpdateManagerCommand.ExecuteAsync(args,
                Environment.ProcessPath ?? throw new InvalidOperationException("无法确定更新管理器路径。"))
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
