namespace ResourceManager.App.Domain.FrameTiming;

/// <summary>帧来自哪一层：图形运行时（DXGI、D3D9）的 Present 调用，或图形内核的 Present。</summary>
public enum FramePresentSource
{
    Dxgi,
    Direct3D9,
    GraphicsKernel
}

/// <summary>一个帧流。DXGI、D3D9 按交换链区分；图形内核不分交换链，SwapChain 为 0。</summary>
public sealed record FrameTimingStream(
    FramePresentSource Source,
    ulong SwapChain,
    DateTimeOffset LastPresentAt,
    FrameIntervalStatistics Statistics);

/// <summary>一个进程实例（PID 加启动时间）的帧流。</summary>
public sealed record FrameTimingProcess(
    int ProcessId,
    long ProcessStartKey,
    IReadOnlyList<FrameTimingStream> Streams);

/// <summary>
/// 一个时间窗内各进程的帧统计。Complete 为 false 表示时间窗内会话丢过事件，统计可能偏少。
/// </summary>
public sealed record FrameTimingSnapshot(
    DateTimeOffset ObservedAt,
    TimeSpan Window,
    bool Complete,
    IReadOnlyList<FrameTimingProcess> Processes);
