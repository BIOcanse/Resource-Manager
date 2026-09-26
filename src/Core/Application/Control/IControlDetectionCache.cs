namespace ResourceManager.App.Application.Control;

/// <summary>
/// 探测结果记在进程里的东西，都实现这个，好让"重新检测"能把它清掉。
///
/// **为什么需要它**：探测是要碰硬件的 —— 问辅助进程、起风扇核心、
/// 用一次空写去问驱动接不接受。这些不能每次画界面都做一遍，所以都记了缓存。
/// 可缓存一记，用户装上组件、插上显卡、换了机器状态之后就看不到变化了，
/// 只能重启程序。
///
/// 于是要有一条"忘掉你记的，重新问一遍"的路。它**只清缓存，不改任何设定** ——
/// 重新检测不该顺手把用户调过的东西动了。
/// </summary>
public interface IControlDetectionCache
{
    void ResetDetection();
}
