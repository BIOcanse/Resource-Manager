namespace ResourceManager.App.Domain.Overlay;

public static class PerformanceOverlayMetricIds
{
    public const string Fps = "target.fps";
    public const string FrameTime = "target.frameTime";
    public const string OnePercentLow = "target.onePercentLow";
    public const string PointOnePercentLow = "target.pointOnePercentLow";
    public const string Cpu = "target.cpu";
    public const string Gpu = "target.gpu";
    public const string Memory = "target.memory";
    public const string Vram = "target.vram";

    public static readonly IReadOnlyList<string> Known =
        [Fps, FrameTime, OnePercentLow, PointOnePercentLow, Cpu, Gpu, Memory, Vram];
}
