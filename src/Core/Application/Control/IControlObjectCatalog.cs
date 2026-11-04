using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 这台机器上有哪些可控对象，各自能控什么。
///
/// 只读。它不改任何东西，也不记住用户想要什么 —— 那是状态机那一侧的事。
/// 一次性消费者异步取得新监控结果；原采样门内只投影已取得的当前值。
/// </summary>
public interface IControlObjectCatalog
{
    /// <summary>Updates declarations once only if topology has never been published.</summary>
    Task EnsureActualTopologyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Finite cached discovery at actual-reader run entry, never per round.</summary>
    Task DiscoverActualReadingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Whether the current all-object actual-value subscription includes
    /// cached FanCore objects. Classification reads declarations only, never probes.</summary>
    bool ActualReadingsUseFanCore => false;

    /// <summary>Whether the current all-object actual-value subscription includes
    /// bridge-backed fields. Classification reads declarations only, never probes.</summary>
    bool ActualReadingsUseHardwareBridge => false;

    /// <summary>Pure declared write dependencies for an admitted target; never probes hardware.</summary>
    bool UsesHardwareBridge(string objectId, string capabilityId) => false;

    /// <summary>Projects the current snapshot inside an already-owned sampling
    /// operation. External one-shot consumers use ReadObjectsAsync.</summary>
    ControlObjectCatalog ReadObjects();

    Task<ControlObjectCatalog> ReadObjectsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadObjects());
    }
}
