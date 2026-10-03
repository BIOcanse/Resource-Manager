using ResourceManager.App.Domain.FrameTiming;

namespace ResourceManager.App.Application.FrameTiming;

/// <summary>
/// 按进程提供帧时间。有订阅才采集；最后一个订阅释放就停。
/// </summary>
public interface IFrameTimingObservationSource
{
    IDisposable AcquireSubscription();

    /// <summary>采集没有运行（无订阅、功能区冻结或会话失败）时返回 null。</summary>
    FrameTimingSnapshot? Read(TimeSpan window);

    /// <summary>Read intervals ending after the cursor and at or before through. Null means capture is unavailable.</summary>
    FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through);
}
