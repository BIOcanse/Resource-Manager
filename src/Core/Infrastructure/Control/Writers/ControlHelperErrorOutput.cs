namespace ResourceManager.App.Infrastructure.Control.Writers;

internal sealed class ControlHelperErrorOutput(TextReader reader)
    : HelperErrorOutput(reader, ProductOptions)
{
    internal const string MissingRuntime = "control-helper-runtime-missing";
    internal const string StartupFailed = "control-helper-startup-failed";
    internal static HelperErrorOutputOptions ProductOptions { get; } =
        new(1024, 2048, 4096, "Microsoft.NETCore.App", "10.0.", MissingRuntime, StartupFailed);
}
