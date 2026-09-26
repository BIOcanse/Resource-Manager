namespace ResourceManager.NativeUi;

internal sealed record NativeUiLaunchOptions(
    bool StartInBackground,
    string? SingleInstanceCommand)
{
    public static NativeUiLaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Any(static arg => arg.Equals("--exit-existing", StringComparison.OrdinalIgnoreCase)))
        {
            return new NativeUiLaunchOptions(false, "exit");
        }

        if (args.Any(static arg => arg.Equals("--show-existing", StringComparison.OrdinalIgnoreCase)))
        {
            return new NativeUiLaunchOptions(false, "show");
        }

        if (args.Any(static arg => arg.Equals("--show-existing-passive", StringComparison.OrdinalIgnoreCase)))
        {
            return new NativeUiLaunchOptions(false, "show-passive");
        }

        if (args.Any(static arg => arg.Equals("--show-existing-passive-secondary", StringComparison.OrdinalIgnoreCase)))
        {
            return new NativeUiLaunchOptions(false, "show-passive-secondary");
        }

        var background = args.Any(static arg => arg.Equals("--background-startup", StringComparison.OrdinalIgnoreCase));
        return new NativeUiLaunchOptions(
            background,
            background ? "ensure-running" : null);
    }
}
