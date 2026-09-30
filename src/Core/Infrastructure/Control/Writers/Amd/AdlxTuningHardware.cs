using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.Control.Writers.Amd;

internal sealed class AdlxTuningHardware : IAdlxTuningHardware
{
    private readonly WindowsGpuAdapterOrderReader inventoryReader = new();
    public AdlxTuningValue Read(ControlObject target, int field)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("ADLX 调节需要 Windows。");
        var inventory = inventoryReader.ReadInventory();
        string? identity = null;
        // The Windows display index is mapped through PNP, never treated as an ADLX index.
        for (var index = 0; index < 128; index++)
        {
            var pnp = new StringBuilder(1024);
            var result = Native.GetPnp(index, pnp, pnp.Capacity);
            if (result == -14) break;
            if (result == -11) continue;
            Check(result);
            var match = WindowsGpuProviderIdentityResolver.ResolvePnp(inventory, pnp.ToString());
            if (!match.IsCurrent || match.Binding.Adapter.Index != target.AdapterIndex) continue;
            if (identity is not null) throw new IOException("显示适配器映射不唯一，拒绝调节。");
            identity = pnp.ToString();
        }
        if (identity is null) throw new IOException("无法将 ADLX 显卡唯一映射到 Windows 适配器。");
        Check(Native.Tune(identity, field, 0, 0, out var current, out var min, out var max, out var step, out var mode));
        if (min > max || step <= 0) throw new IOException("ADLX 返回的调节范围无效。");
        return new(identity, current, min, max, step, (AdlxGfxMode)mode);
    }
    public void Write(string pnp, int field, int value)
        => Check(Native.Tune(pnp, field, 1, value, out _, out _, out _, out _, out _));
    private static void Check(int result)
    {
        if (result is 0 or 1 or 2) return;
        if (result == -16) throw new AdlxUnknownGenerationException();
        throw new IOException(result switch
        {
            -10 => "此显卡/驱动不提供 ADLX 的这一调节接口（旧式状态表接口尚待接入）。",
            -11 => "ADLX 显卡身份不存在、不唯一，或不是独显。",
            -12 => "ADLX 调节范围、步长或最小/最大频率关系不允许此值。",
            -13 => "ADLX 写入后回读不一致；驱动可能拒绝或覆盖了设置。",
            -15 => "此显卡的核心频率/电压采用另一种语义，请选择对应的绝对值或偏移量参数。",
            _ => $"ADLX 请求失败：{result}。"
        });
    }
    private static class Native
    {
        [DllImport("ResourceManager.AdlxBridge.dll", EntryPoint = "ResourceManagerAdlxGetTuningGpuPnp", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int GetPnp(int index, StringBuilder pnp, int capacity);
        [DllImport("ResourceManager.AdlxBridge.dll", EntryPoint = "ResourceManagerAdlxTuning", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Tune(string pnp, int field, int operation, int requested, out int current, out int minimum, out int maximum, out int step, out int gfxMode);
    }
}
