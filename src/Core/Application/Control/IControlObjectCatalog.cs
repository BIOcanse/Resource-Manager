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
    /// <summary>Projects the current snapshot inside an already-owned sampling
    /// operation. External one-shot consumers use ReadObjectsAsync.</summary>
    ControlObjectCatalog ReadObjects();

    Task<ControlObjectCatalog> ReadObjectsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadObjects());
    }
}
