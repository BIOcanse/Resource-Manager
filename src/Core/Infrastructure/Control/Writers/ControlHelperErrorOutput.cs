namespace ResourceManager.App.Infrastructure.Control.Writers;

internal sealed class ControlHelperErrorOutput(TextReader reader)
    : HelperErrorOutput(reader, ProductOptions)
{
    internal const string MissingRuntime = "组件缺少 .NET 10 运行时，请安装运行时或使用自带运行时的新版组件。";
    internal const string StartupFailed = "组件运行时未能启动，请更新或修复该组件。";
    internal static HelperErrorOutputOptions ProductOptions { get; } =
        new(1024, 2048, 4096, "Microsoft.NETCore.App", "10.0.", MissingRuntime, StartupFailed);
}
