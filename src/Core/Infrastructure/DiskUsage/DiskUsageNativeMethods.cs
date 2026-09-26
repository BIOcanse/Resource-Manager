using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.DiskUsage;

internal static class NativeMethods
{
    /// <summary>
    /// 把一个 DOS 设备名（盘符）解析成它指向的 NT 设备路径。
    /// subst 出来的盘符指向 <c>\??\</c> 开头的普通目录，真盘指向 <c>\Device\HarddiskVolumeN</c>。
    /// </summary>
    // 出参是 StringBuilder，源生成的 LibraryImport 封送不了，按项目里其他同类签名用 DllImport。
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint QueryDosDeviceW(
        string deviceName,
        StringBuilder targetPath,
        int maxCharacters);
}
