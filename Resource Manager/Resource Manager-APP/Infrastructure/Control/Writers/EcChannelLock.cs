namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// EC 通道的**机器级**互斥。
///
/// EC 是一块硬件，一台机器上只有一份。它的每次访问是"下发命令、再取回结果"两步，
/// 中间被另一个进程插进来，取回的就是别人的结果 —— 表现出来是写入回读对不上、
/// 读数忽然变成另一个寄存器的值，而两边都觉得自己没做错。
///
/// 进程内的 lock 挡不住这个。这台机器上用同一条 ACPI WMI 接口的至少有两个进程：
/// 本程序（cTGP / Dynamic Boost 那条路）和**风扇控制核心**。
/// 实测就是这么撞的：风扇核心单独跑 12 次开关全对，接进本程序后约四成失败。
///
/// 所以锁跨进程，名字按**通道**取。<see cref="ChannelName"/> 是和风扇核心之间的合同，
/// 两边必须一模一样 —— 名字对不上就等于没有锁。
/// </summary>
internal sealed class EcChannelLock : IDisposable
{
    /// <summary>
    /// 通道名。风扇控制核心里的 <c>UniwillWmiEcTransport.ChannelName</c> 是同一个字符串。
    /// </summary>
    internal const string ChannelName = "Uniwill-AcpiWmi-EcChannel";

    /// <summary>
    /// 等一把锁最多多久。一次 EC 事务是毫秒级的，等到这个数说明对方卡死了；
    /// 无限等会把采样线程挂住，那比这次读不到更糟。
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex mutex;
    private bool disposed;

    internal EcChannelLock()
        => mutex = Create($@"Global\{ChannelName}") ?? new Mutex(false, $@"Local\{ChannelName}");

    /// <summary>
    /// 独占这条通道做一件事。超时就不做 —— 宁可如实失败，不和别人抢着写 EC。
    /// </summary>
    internal bool Hold<TResult>(Func<TResult> transaction, out TResult result)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        result = default!;
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(WaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者没释放就退出了。锁归我们，通道状态由各自的回读负责核对。
                acquired = true;
            }
            if (!acquired)
            {
                return false;
            }
            result = transaction();
            return true;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static Mutex? Create(string name)
    {
        try
        {
            return new Mutex(false, name);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        mutex.Dispose();
    }
}
