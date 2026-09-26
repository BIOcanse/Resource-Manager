using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// NVAPI 的写入侧。
///
/// 读取那一侧已经有 <see cref="NvidiaNvapiReader"/>，这里不重复它的采集，
/// 只做它不做的事：往卡里写功耗上限和频率偏移。两边各自持有自己的 NVAPI 句柄 ——
/// NVAPI 本身允许多次 Initialize，而共享一个连接会让采集节奏和写入节奏互相等锁。
///
/// 结构体布局用裸缓冲区按偏移读写，和读取侧同一口径：NVAPI 的这几个接口没有公开头文件，
/// 用 <c>[StructLayout]</c> 声明反而会把"猜出来的字段名"固化成看起来可信的代码。
/// 偏移量的依据写在各自常量上。
/// </summary>
internal sealed class NvidiaNvapiControlBridge
{
    private const int NvapiOk = 0;
    private const int NvapiMaxPhysicalGpus = 64;

    private const uint NvapiInitializeId = 0x0150E828;
    private const uint NvapiEnumPhysicalGpusId = 0xE5AC921F;
    private const uint NvapiGpuGetPciIdentifiersId = 0x2DDFB66E;
    private const uint NvapiGpuGetBusIdId = 0x1BE0B8E5;
    private const uint NvapiGpuGetBusSlotIdId = 0x2A0A350F;
    private const uint NvapiGpuClientPowerPoliciesGetInfoId = 0x34206D86;
    private const uint NvapiGpuClientPowerPoliciesGetStatusId = 0x70916171;
    private const uint NvapiGpuClientPowerPoliciesSetStatusId = 0xAD95F5ED;
    private const uint NvapiGpuSetPstates20Id = 0x0F4DAE6B;
    private const uint NvapiGpuGetPstates20Id = 0x6FF81213;

    /// <summary>
    /// 功耗策略信息表。头 8 字节是 version / valid / count，后面四个档位各 44 字节。
    /// 档位里我们只用到 min / def / max 三个字段，其余位置厂商没有公开含义，保持不动。
    /// </summary>
    private const int PowerInfoEntrySize = 44;
    private const int PowerInfoEntriesOffset = 8;
    private const int PowerInfoCountOffset = 5;
    private const int PowerInfoMaxEntries = 4;
    private const int PowerInfoSize = PowerInfoEntriesOffset
        + (PowerInfoMaxEntries * PowerInfoEntrySize);
    private const int PowerInfoEntryMinimumOffset = 12;
    private const int PowerInfoEntryDefaultOffset = 24;
    private const int PowerInfoEntryMaximumOffset = 36;

    /// <summary>
    /// 功耗策略当前值。头 8 字节是 version / count，后面四个档位各 16 字节。
    ///
    /// 这两个尺寸是**问驱动问出来的**，不是照二手资料抄的：NVAPI 对版本对不上的调用
    /// 返回 INCOMPATIBLE_STRUCT_VERSION，把 (大小, 版本) 试过去，它接受哪个就是哪个。
    /// 184/1 和 72/1 是本机 NVIDIA 驱动接受的组合。
    /// </summary>
    private const int PowerStatusEntrySize = 16;
    private const int PowerStatusEntriesOffset = 8;
    private const int PowerStatusCountOffset = 4;
    private const int PowerStatusMaxEntries = 4;
    private const int PowerStatusSize = PowerStatusEntriesOffset
        + (PowerStatusMaxEntries * PowerStatusEntrySize);

    /// <summary>
    /// P-State 表。这几个尺寸和读取侧（<c>NvidiaNvapiReader.Sensors</c>）是同一份布局，
    /// 那边靠它读电压，这边靠它写频率偏移 —— 对不上的话两边会同时出错，不会静默分叉。
    /// </summary>
    private const int Pstates20V1Size = 7316;
    private const int Pstates20HeaderSize = 20;
    private const int Pstate20EntrySize = 456;
    private const int Pstate20ClocksOffset = 8;
    private const int Pstate20ClockEntrySize = 44;
    private const int Pstate20ClockFrequencyDeltaOffset = 12;
    private const int Pstates20NumPstatesOffset = 8;
    private const int Pstates20NumClocksOffset = 12;
    private const int Pstates20NumBaseVoltagesOffset = 16;

    /// <summary>NVAPI 的时钟域编号。0 是核心，4 是显存。</summary>
    internal const uint GraphicsClockDomain = 0;
    internal const uint MemoryClockDomain = 4;

    private static readonly TimeSpan HandleMapLifetime = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader adapterReader = new();
    private bool initAttempted;
    private bool initialized;
    private Bindings bindings = Bindings.Unavailable;
    private Dictionary<int, IntPtr>? handleMap;
    private DateTime handleMapReadAt;

    /// <summary>
    /// 找出这个显示适配器序号对应的 NVAPI 句柄。
    ///
    /// 结果只缓存 <see cref="HandleMapLifetime"/> 这么久：显卡会热插拔，
    /// 留久了句柄就是个坏指针；但界面每刷新一次都要问好几项能力，
    /// 每项都重新枚举一遍驱动又太亏。这个长度短到拔插察觉不出延迟。
    /// </summary>
    internal IntPtr? FindHandleByAdapterIndex(int adapterIndex)
    {
        lock (gate)
        {
            return ReadHandleMap().TryGetValue(adapterIndex, out var handle) ? handle : null;
        }
    }

    private IReadOnlyDictionary<int, IntPtr> ReadHandleMap()
    {
        if (handleMap is not null && DateTime.UtcNow - handleMapReadAt < HandleMapLifetime)
        {
            return handleMap;
        }

        handleMapReadAt = DateTime.UtcNow;
        handleMap = EnumerateHandles();
        return handleMap;
    }

    private Dictionary<int, IntPtr> EnumerateHandles()
    {
        var found = new Dictionary<int, IntPtr>();
        if (!EnsureInitialized() || bindings.EnumPhysicalGpus is null)
        {
            return found;
        }

        var handles = new IntPtr[NvapiMaxPhysicalGpus];
        if (bindings.EnumPhysicalGpus(handles, out var count) != NvapiOk || count == 0)
        {
            return found;
        }

        var inventory = adapterReader.ReadInventory();
        for (var index = 0; index < count && index < NvapiMaxPhysicalGpus; index++)
        {
            var handle = handles[index];
            if (handle == IntPtr.Zero
                || !TryResolveAdapterIndex(handle, inventory, out var resolved))
            {
                continue;
            }
            found[resolved] = handle;
        }
        return found;
    }

    /// <summary>
    /// 把功耗策略那两块内存原样倒出来。
    ///
    /// 这几个结构体没有公开头文件，偏移量是照着已知布局写的 —— 判断"这块卡锁死了功耗"
    /// 之前必须能看见原始值，否则偏移写错了也会得出同样的结论，而且看不出来。
    /// 诊断用，不进监控链路。
    /// </summary>
    internal NvidiaPowerPolicyDump? DumpPowerPolicy(IntPtr gpuHandle)
    {
        lock (gate)
        {
            if (!EnsureInitialized()
                || bindings.ClientPowerPoliciesGetInfo is null
                || bindings.ClientPowerPoliciesGetStatus is null)
            {
                return null;
            }

            var info = Marshal.AllocHGlobal(PowerInfoSize);
            var status = Marshal.AllocHGlobal(PowerStatusSize);
            try
            {
                ZeroWithVersion(info, PowerInfoSize, MakeVersion(PowerInfoSize, 1));
                ZeroWithVersion(status, PowerStatusSize, MakeVersion(PowerStatusSize, 1));
                var infoResult = bindings.ClientPowerPoliciesGetInfo(gpuHandle, info);
                var statusResult = bindings.ClientPowerPoliciesGetStatus(gpuHandle, status);
                return new NvidiaPowerPolicyDump(
                    infoResult,
                    statusResult,
                    DumpWords(info, PowerInfoSize),
                    DumpWords(status, PowerStatusSize));
            }
            finally
            {
                Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(status);
            }
        }
    }

    /// <summary>
    /// 功耗策略的每一种结构体版本，各自把非零位置原样倒出来。
    ///
    /// 驱动同时接受 v1 和 v2 两套布局，而**填数据的可能只有其中一套** ——
    /// 只看 v1 的前几个字就断言"这块卡不开放功耗"，是拿看不全的证据下结论。
    /// 只读，诊断用。
    /// </summary>
    internal IReadOnlyList<object> DumpPowerPolicyVariants(IntPtr gpuHandle)
    {
        lock (gate)
        {
            var dumps = new List<object>();
            if (!EnsureInitialized())
            {
                return dumps;
            }

            var variants = new (string Name, BufferCallDelegate? Call, int Size, uint Version)[]
            {
                ("GetInfo", bindings.ClientPowerPoliciesGetInfo, 184, 1),
                ("GetInfo", bindings.ClientPowerPoliciesGetInfo, 2248, 2),
                ("GetStatus", bindings.ClientPowerPoliciesGetStatus, 72, 1),
                ("GetStatus", bindings.ClientPowerPoliciesGetStatus, 1368, 2)
            };

            foreach (var (name, call, size, version) in variants)
            {
                if (call is null)
                {
                    continue;
                }
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    ZeroWithVersion(buffer, size, MakeVersion(size, version));
                    var result = call(gpuHandle, buffer);
                    var nonZero = new List<string>();
                    for (var offset = 4; offset < size; offset += 4)
                    {
                        var word = ReadUInt32(buffer, offset);
                        if (word != 0)
                        {
                            nonZero.Add($"+{offset}={word}");
                        }
                    }
                    dumps.Add(new { Name = name, Size = size, Version = version, Result = result, NonZero = nonZero });
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return dumps;
        }
    }

    private static IReadOnlyList<uint> DumpWords(IntPtr buffer, int size)
    {
        var words = new uint[size / 4];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = ReadUInt32(buffer, index * 4);
        }
        return words;
    }

    /// <summary>这块卡允许把功耗上限调到什么范围，以及现在是多少。单位都是百分比。</summary>
    internal NvidiaPowerLimitRange? ReadPowerLimitRange(IntPtr gpuHandle)
    {
        lock (gate)
        {
            if (!EnsureInitialized()
                || bindings.ClientPowerPoliciesGetInfo is null
                || bindings.ClientPowerPoliciesGetStatus is null)
            {
                return null;
            }

            var info = Marshal.AllocHGlobal(PowerInfoSize);
            var status = Marshal.AllocHGlobal(PowerStatusSize);
            try
            {
                ZeroWithVersion(info, PowerInfoSize, MakeVersion(PowerInfoSize, 1));
                ZeroWithVersion(status, PowerStatusSize, MakeVersion(PowerStatusSize, 1));

                if (bindings.ClientPowerPoliciesGetInfo(gpuHandle, info) != NvapiOk
                    || bindings.ClientPowerPoliciesGetStatus(gpuHandle, status) != NvapiOk)
                {
                    return null;
                }

                if (Marshal.ReadByte(info, PowerInfoCountOffset) == 0
                    || ReadUInt32(status, PowerStatusCountOffset) == 0)
                {
                    return null;
                }

                // 第一个档位就是 P0，也就是用户在说"功耗上限"时指的那个。
                var minimum = ReadUInt32(info, PowerInfoEntriesOffset + PowerInfoEntryMinimumOffset);
                var standard = ReadUInt32(info, PowerInfoEntriesOffset + PowerInfoEntryDefaultOffset);
                var maximum = ReadUInt32(info, PowerInfoEntriesOffset + PowerInfoEntryMaximumOffset);
                if (minimum == 0 || maximum == 0 || maximum < minimum)
                {
                    return null;
                }

                return new NvidiaPowerLimitRange(
                    minimum / 1000.0,
                    maximum / 1000.0,
                    standard == 0 ? 100 : standard / 1000.0);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(status);
            }
        }
    }

    /// <summary>
    /// 把某个时钟域现在生效的频率偏移读回来，单位 MHz。
    ///
    /// 写完之后拿它核对：驱动对 SetPstates20 返回成功并不等于它真的把值收下了，
    /// 只有从 P-State 表里再读出来才算数。界面上"已应用"这三个字也该有这个分量。
    /// </summary>
    internal double? ReadClockOffsetMhz(IntPtr gpuHandle, uint clockDomain)
    {
        lock (gate)
        {
            if (!EnsureInitialized() || bindings.GetPstates20 is null)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(Pstates20V1Size);
            try
            {
                ZeroWithVersion(buffer, Pstates20V1Size, MakeVersion(Pstates20V1Size, 1));
                if (bindings.GetPstates20(gpuHandle, buffer) != NvapiOk)
                {
                    return null;
                }

                var pstateCount = Math.Min(ReadUInt32(buffer, Pstates20NumPstatesOffset), 16u);
                var clockCount = Math.Min(ReadUInt32(buffer, Pstates20NumClocksOffset), 8u);
                for (var pstate = 0; pstate < pstateCount; pstate++)
                {
                    var pstateOffset = Pstates20HeaderSize + (pstate * Pstate20EntrySize);
                    // P0 才是用户说的"超频"那一档，其余档位不看。
                    if (ReadUInt32(buffer, pstateOffset) != 0)
                    {
                        continue;
                    }
                    for (var clock = 0; clock < clockCount; clock++)
                    {
                        var clockOffset = pstateOffset
                            + Pstate20ClocksOffset
                            + (clock * Pstate20ClockEntrySize);
                        if (ReadUInt32(buffer, clockOffset) != clockDomain)
                        {
                            continue;
                        }
                        return Marshal.ReadInt32(
                            buffer,
                            clockOffset + Pstate20ClockFrequencyDeltaOffset) / 1000.0;
                    }
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>把某个时钟域的频率偏移写下去，单位 MHz。</summary>
    internal bool TryWriteClockOffsetMhz(IntPtr gpuHandle, uint clockDomain, double offsetMhz)
    {
        lock (gate)
        {
            if (!EnsureInitialized() || bindings.SetPstates20 is null)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(Pstates20V1Size);
            try
            {
                ZeroWithVersion(buffer, Pstates20V1Size, MakeVersion(Pstates20V1Size, 1));
                // 一个 P-State（P0）、一个时钟域、不动电压。
                WriteUInt32(buffer, Pstates20NumPstatesOffset, 1);
                WriteUInt32(buffer, Pstates20NumClocksOffset, 1);
                WriteUInt32(buffer, Pstates20NumBaseVoltagesOffset, 0);

                var clockOffset = Pstates20HeaderSize + Pstate20ClocksOffset;
                WriteUInt32(buffer, clockOffset, clockDomain);
                WriteInt32(
                    buffer,
                    clockOffset + Pstate20ClockFrequencyDeltaOffset,
                    (int)Math.Round(offsetMhz * 1000.0));
                return bindings.SetPstates20(gpuHandle, buffer) == NvapiOk;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private bool TryResolveAdapterIndex(
        IntPtr handle,
        WindowsGpuAdapterInventoryRead inventory,
        out int adapterIndex)
    {
        adapterIndex = -1;
        if (bindings.GetPciIdentifiers is null
            || bindings.GetBusId is null
            || bindings.GetBusSlotId is null)
        {
            return false;
        }

        if (bindings.GetPciIdentifiers(
                handle,
                out var deviceId,
                out var subSystemId,
                out _,
                out _) != NvapiOk
            || bindings.GetBusId(handle, out var busId) != NvapiOk
            || bindings.GetBusSlotId(handle, out var busSlotId) != NvapiOk)
        {
            return false;
        }

        if (!WindowsGpuProviderIdentityResolver.TryCreateNvapiPciEvidence(
                busId,
                busSlotId,
                deviceId,
                subSystemId,
                out var evidence))
        {
            return false;
        }

        var match = WindowsGpuProviderIdentityResolver.ResolvePci(inventory, evidence);
        if (!match.IsCurrent)
        {
            return false;
        }

        adapterIndex = match.Binding.Adapter.Index;
        return true;
    }

    private bool EnsureInitialized()
    {
        if (initAttempted)
        {
            return initialized;
        }

        initAttempted = true;
        if (!TryLoadBindings(out bindings))
        {
            initialized = false;
            return false;
        }

        initialized = bindings.Initialize is not null && bindings.Initialize() == NvapiOk;
        return initialized;
    }

    private static bool TryLoadBindings(out Bindings result)
    {
        var fileName = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        if (!TryLoad(fileName, out var library)
            && !TryLoad(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    fileName),
                out library))
        {
            result = Bindings.Unavailable;
            return false;
        }

        if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryAddress))
        {
            NativeLibrary.Free(library);
            result = Bindings.Unavailable;
            return false;
        }

        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryAddress);
        result = new Bindings(
            Get<InitializeDelegate>(query, NvapiInitializeId),
            Get<EnumPhysicalGpusDelegate>(query, NvapiEnumPhysicalGpusId),
            Get<GetPciIdentifiersDelegate>(query, NvapiGpuGetPciIdentifiersId),
            Get<GetBusIdDelegate>(query, NvapiGpuGetBusIdId),
            Get<GetBusSlotIdDelegate>(query, NvapiGpuGetBusSlotIdId),
            Get<BufferCallDelegate>(query, NvapiGpuClientPowerPoliciesGetInfoId),
            Get<BufferCallDelegate>(query, NvapiGpuClientPowerPoliciesGetStatusId),
            Get<BufferCallDelegate>(query, NvapiGpuClientPowerPoliciesSetStatusId),
            Get<BufferCallDelegate>(query, NvapiGpuSetPstates20Id),
            Get<BufferCallDelegate>(query, NvapiGpuGetPstates20Id));
        return true;
    }

    private static bool TryLoad(string candidate, out IntPtr handle)
    {
        try
        {
            return NativeLibrary.TryLoad(candidate, out handle);
        }
        catch (Exception error) when (error is BadImageFormatException or DllNotFoundException)
        {
            handle = IntPtr.Zero;
            return false;
        }
    }

    private static TDelegate? Get<TDelegate>(QueryInterfaceDelegate query, uint functionId)
        where TDelegate : Delegate
    {
        var address = query(functionId);
        return address == IntPtr.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    /// <summary>
    /// NVAPI 的结构体第一个字段永远是 version，其值是 <c>大小 | 版本号 &lt;&lt; 16</c>。
    /// 驱动靠它判断这块内存是什么布局，填错就直接拒绝 —— 所以清零之后必须马上写回去。
    /// </summary>
    private static uint MakeVersion(int structSize, uint version)
        => (uint)structSize | (version << 16);

    private static void ZeroWithVersion(IntPtr buffer, int size, uint version)
    {
        for (var offset = 0; offset < size; offset += 4)
        {
            Marshal.WriteInt32(buffer, offset, 0);
        }
        Marshal.WriteInt32(buffer, 0, unchecked((int)version));
    }

    private static uint ReadUInt32(IntPtr buffer, int offset)
        => unchecked((uint)Marshal.ReadInt32(buffer, offset));

    private static void WriteUInt32(IntPtr buffer, int offset, uint value)
        => Marshal.WriteInt32(buffer, offset, unchecked((int)value));

    private static void WriteInt32(IntPtr buffer, int offset, int value)
        => Marshal.WriteInt32(buffer, offset, value);

    private readonly record struct Bindings(
        InitializeDelegate? Initialize,
        EnumPhysicalGpusDelegate? EnumPhysicalGpus,
        GetPciIdentifiersDelegate? GetPciIdentifiers,
        GetBusIdDelegate? GetBusId,
        GetBusSlotIdDelegate? GetBusSlotId,
        BufferCallDelegate? ClientPowerPoliciesGetInfo,
        BufferCallDelegate? ClientPowerPoliciesGetStatus,
        BufferCallDelegate? ClientPowerPoliciesSetStatus,
        BufferCallDelegate? SetPstates20,
        BufferCallDelegate? GetPstates20)
    {
        public static Bindings Unavailable { get; } =
            new(null, null, null, null, null, null, null, null, null, null);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint interfaceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] gpuHandles, out uint gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPciIdentifiersDelegate(
        IntPtr gpuHandle,
        out uint deviceId,
        out uint subSystemId,
        out uint revisionId,
        out uint externalDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusIdDelegate(IntPtr gpuHandle, out uint busId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusSlotIdDelegate(IntPtr gpuHandle, out uint busSlotId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BufferCallDelegate(IntPtr gpuHandle, IntPtr buffer);
}

/// <summary>功耗策略那两块内存的原样转储，诊断用。</summary>
internal sealed record NvidiaPowerPolicyDump(
    int InfoResult,
    int StatusResult,
    IReadOnlyList<uint> InfoWords,
    IReadOnlyList<uint> StatusWords);

/// <summary>这块卡的功耗上限能调到哪儿。全部是默认功耗的百分比。</summary>
internal readonly record struct NvidiaPowerLimitRange(
    double MinimumPercent,
    double MaximumPercent,
    double DefaultPercent)
{
    /// <summary>上下限一样就是这块卡锁死了功耗，调不动。</summary>
    internal bool IsAdjustable => MaximumPercent > MinimumPercent;
}
