using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// Intel 核显的控制通道：IGCL（Intel Graphics Control Library）。
///
/// **二进制随 Intel 显卡驱动一起装**（<c>ControlLib.dll</c>），和 NVAPI / NVML 同一模式 ——
/// 不需要额外下载组件，也不需要我们自己写内核。头文件和包装层是 Intel 开源的
/// （<c>intel/drivers.gpu.control-library</c>），这里照它写，不猜结构体。
///
/// Alder Lake-P 及以后的平台才有。老平台上 <c>ctlInit</c> 会失败，如实报不可用。
///
/// **超频类接口在调用 <see cref="TryAcceptOverclockWaiver"/> 之前一律被拒绝。**
/// 那是 Intel 的硬性要求，原文是"用户据此接受部件寿命缩短"，并且要求应用
/// 必须先告知用户、取得同意才能调。所以这里不替用户默认接受 —— 由上层在
/// 用户明确同意之后调一次。
/// </summary>
internal sealed class IntelGraphicsControlBridge
{
    private const int CtlResultSuccess = 0;

    /// <summary>头文件里的 <c>CTL_MAKE_VERSION(1, 1)</c>。</summary>
    private const uint ApiVersion = (1u << 16) | 1u;

    private readonly object gate = new();
    private bool initAttempted;
    private IntPtr apiHandle;
    private IntPtr[] devices = [];

    /// <summary>这台机器上有没有这条通道。</summary>
    internal bool IsAvailable
    {
        get
        {
            lock (gate)
            {
                return EnsureInitialized() && devices.Length > 0;
            }
        }
    }

    /// <summary>
    /// 第几块 Intel 显示适配器。
    ///
    /// IGCL 的枚举顺序和系统的适配器序号不是一回事，所以这里只按 IGCL 自己的次序取；
    /// 核显在一台机器上就一块，取第一块即可。取不到就返回 null。
    /// </summary>
    internal IntPtr? FirstDevice()
    {
        lock (gate)
        {
            return EnsureInitialized() && devices.Length > 0 ? devices[0] : null;
        }
    }

    /// <summary>
    /// 告诉驱动：用户已经知道并接受超频带来的寿命损耗。
    ///
    /// **只有在用户明确同意之后才该调。** 不调的话所有超频接口都会被拒绝，
    /// 那是 Intel 设计的保护，不是故障。
    /// </summary>
    internal bool TryAcceptOverclockWaiver(IntPtr device)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return false;
            }
            try
            {
                return NativeMethods.ctlOverclockWaiverSet(device) == CtlResultSuccess;
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>当前的核心频率偏移，单位 MHz。读不到返回 null。</summary>
    internal double? ReadFrequencyOffsetMhz(IntPtr device)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            try
            {
                return NativeMethods.ctlOverclockGpuFrequencyOffsetGet(device, out var offset)
                    == CtlResultSuccess
                        ? offset
                        : null;
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>写核心频率偏移，单位 MHz。返回驱动给的原始结果码，0 是成功。</summary>
    internal int WriteFrequencyOffsetMhz(IntPtr device, double offsetMhz)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return -1;
            }
            try
            {
                return NativeMethods.ctlOverclockGpuFrequencyOffsetSet(device, offsetMhz);
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return -1;
            }
        }
    }

    private bool EnsureInitialized()
    {
        if (initAttempted)
        {
            return apiHandle != IntPtr.Zero;
        }

        initAttempted = true;
        try
        {
            var arguments = new CtlInitArgs
            {
                Size = (uint)Marshal.SizeOf<CtlInitArgs>(),
                Version = 0,
                AppVersion = ApiVersion,
                Flags = 0
            };
            if (NativeMethods.ctlInit(ref arguments, out apiHandle) != CtlResultSuccess
                || apiHandle == IntPtr.Zero)
            {
                apiHandle = IntPtr.Zero;
                return false;
            }

            // 先问个数，再按数取 —— 这是这套接口的约定。
            uint count = 0;
            if (NativeMethods.ctlEnumerateDevices(apiHandle, ref count, null) != CtlResultSuccess
                || count == 0)
            {
                devices = [];
                return true;
            }
            var found = new IntPtr[count];
            devices = NativeMethods.ctlEnumerateDevices(apiHandle, ref count, found)
                == CtlResultSuccess
                    ? found[..(int)count]
                    : [];
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            // 没装 Intel 显卡驱动，或者平台太老没有这套接口。
            apiHandle = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// 对应头文件里的 <c>ctl_init_args_t</c>。字段顺序和类型照抄，不重排 ——
    /// <c>Size</c> 是驱动用来判断布局的，排错了它会直接拒绝。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        internal uint Size;
        internal byte Version;
        internal uint AppVersion;
        internal uint Flags;
        internal uint SupportedVersion;
        internal uint ApplicationUidData1;
        internal ushort ApplicationUidData2;
        internal ushort ApplicationUidData3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal byte[] ApplicationUidData4;
    }

    private static class NativeMethods
    {
        private const string Library = "ControlLib.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ctlInit(ref CtlInitArgs initDescription, out IntPtr apiHandle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ctlEnumerateDevices(
            IntPtr apiHandle,
            ref uint count,
            [In, Out] IntPtr[]? devices);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ctlOverclockWaiverSet(IntPtr deviceHandle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ctlOverclockGpuFrequencyOffsetGet(
            IntPtr deviceHandle,
            out double frequencyOffset);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ctlOverclockGpuFrequencyOffsetSet(
            IntPtr deviceHandle,
            double frequencyOffset);
    }
}
