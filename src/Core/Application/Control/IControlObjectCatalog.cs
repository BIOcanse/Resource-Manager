using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 这台机器上有哪些可控对象，各自能控什么。
///
/// 只读。它不改任何东西，也不记住用户想要什么 —— 那是状态机那一侧的事。
/// 每次都现读：显卡会热插拔（eGPU），风扇会因为组件装上而突然可控。
/// </summary>
public interface IControlObjectCatalog
{
    ControlObjectCatalog ReadObjects();
}
