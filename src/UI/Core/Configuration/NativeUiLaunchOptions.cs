namespace ResourceManager.NativeUi;

/// <summary>
/// The single-instance command named by a command line that started this program without the resident's pipe.
/// The flags are the ones persistent entries and scripts already pass; <see cref="ResidentHandoff"/> forwards the
/// command to the resident unchanged.
/// </summary>
internal static class NativeUiLaunchOptions
{
    public static string ParseCommand(IReadOnlyList<string> args)
    {
        if (Has(args, "--exit-existing")) return "exit";
        if (Has(args, "--show-existing")) return "show";
        if (Has(args, "--show-existing-passive")) return "show-passive";
        if (Has(args, "--show-existing-passive-secondary")) return "show-passive-secondary";
        if (Has(args, "--background-startup")) return "ensure-running";
        return "show";
    }

    private static bool Has(IReadOnlyList<string> args, string flag)
        => args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
