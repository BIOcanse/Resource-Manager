using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// A subscription's managed callbacks. Binding never modifies the live TraceEvent parser.
/// </summary>
public sealed class KernelEtwEventSource
{
    // Building callbacks are unpublished; a cancelled source is never activated again.
    private int state;
    public bool IsActive => Volatile.Read(ref state) == 1;

    public event Action<ProcessTraceData>? ProcessStartGroup;
    public event Action<ProcessTraceData>? ProcessEndGroup;
    public event Action<ThreadTraceData>? ThreadStartGroup;
    public event Action<ThreadTraceData>? ThreadEndGroup;
    public event Action<CSwitchTraceData>? ThreadCSwitch;
    public event Action<DiskIOTraceData>? DiskIORead;
    public event Action<DiskIOTraceData>? DiskIOWrite;
    public event Action<FileIOCreateTraceData>? FileIOCreate;
    public event Action<FileIONameTraceData>? FileIOFileCreate;
    public event Action<FileIONameTraceData>? FileIOFileDelete;
    public event Action<FileIOReadWriteTraceData>? FileIOWrite;
    public event Action<FileIOInfoTraceData>? FileIORename;
    public event Action<FileIOInfoTraceData>? FileIODelete;
    public event Action<TcpIpSendTraceData>? TcpIpSend;
    public event Action<TcpIpTraceData>? TcpIpRecv;
    public event Action<TcpIpV6SendTraceData>? TcpIpSendIPV6;
    public event Action<TcpIpV6TraceData>? TcpIpRecvIPV6;
    public event Action<UdpIpTraceData>? UdpIpSend;
    public event Action<UdpIpTraceData>? UdpIpRecv;
    public event Action<UpdIpV6TraceData>? UdpIpSendIPV6;
    public event Action<UpdIpV6TraceData>? UdpIpRecvIPV6;
    public event Action<ImageLoadTraceData>? ImageDCStart;
    public event Action<ImageLoadTraceData>? ImageLoad;
    public event Action<ImageLoadTraceData>? ImageUnload;
    public event Action<ISRTraceData>? PerfInfoISR;
    public event Action<DPCTraceData>? PerfInfoDPC;
    public event Action<DPCTraceData>? PerfInfoThreadedDPC;
    public event Action<DPCTraceData>? PerfInfoTimerDPC;

    internal void Activate()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("The kernel event source cannot be activated twice.");
    }

    internal void Cancel() => Interlocked.Exchange(ref state, -1);

    internal bool Dispatch<T>(T data, Func<KernelEtwEventSource, T, bool> deliver)
    {
        // This read admits the synchronous delivery. Cancellation prevents later
        // admissions; a delivery already admitted may finish without a blocking wait.
        return IsActive && deliver(this, data);
    }

    internal bool DeliverProcessStartGroup(ProcessTraceData data) => Deliver(ProcessStartGroup, data);
    internal bool DeliverProcessEndGroup(ProcessTraceData data) => Deliver(ProcessEndGroup, data);
    internal bool DeliverThreadStartGroup(ThreadTraceData data) => Deliver(ThreadStartGroup, data);
    internal bool DeliverThreadEndGroup(ThreadTraceData data) => Deliver(ThreadEndGroup, data);
    internal bool DeliverThreadCSwitch(CSwitchTraceData data) => Deliver(ThreadCSwitch, data);
    internal bool DeliverDiskIORead(DiskIOTraceData data) => Deliver(DiskIORead, data);
    internal bool DeliverDiskIOWrite(DiskIOTraceData data) => Deliver(DiskIOWrite, data);
    internal bool DeliverFileIOCreate(FileIOCreateTraceData data) => Deliver(FileIOCreate, data);
    internal bool DeliverFileIOFileCreate(FileIONameTraceData data) => Deliver(FileIOFileCreate, data);
    internal bool DeliverFileIOFileDelete(FileIONameTraceData data) => Deliver(FileIOFileDelete, data);
    internal bool DeliverFileIOWrite(FileIOReadWriteTraceData data) => Deliver(FileIOWrite, data);
    internal bool DeliverFileIORename(FileIOInfoTraceData data) => Deliver(FileIORename, data);
    internal bool DeliverFileIODelete(FileIOInfoTraceData data) => Deliver(FileIODelete, data);
    internal bool DeliverTcpIpSend(TcpIpSendTraceData data) => Deliver(TcpIpSend, data);
    internal bool DeliverTcpIpRecv(TcpIpTraceData data) => Deliver(TcpIpRecv, data);
    internal bool DeliverTcpIpSendIPV6(TcpIpV6SendTraceData data) => Deliver(TcpIpSendIPV6, data);
    internal bool DeliverTcpIpRecvIPV6(TcpIpV6TraceData data) => Deliver(TcpIpRecvIPV6, data);
    internal bool DeliverUdpIpSend(UdpIpTraceData data) => Deliver(UdpIpSend, data);
    internal bool DeliverUdpIpRecv(UdpIpTraceData data) => Deliver(UdpIpRecv, data);
    internal bool DeliverUdpIpSendIPV6(UpdIpV6TraceData data) => Deliver(UdpIpSendIPV6, data);
    internal bool DeliverUdpIpRecvIPV6(UpdIpV6TraceData data) => Deliver(UdpIpRecvIPV6, data);
    internal bool DeliverImageDCStart(ImageLoadTraceData data) => Deliver(ImageDCStart, data);
    internal bool DeliverImageLoad(ImageLoadTraceData data) => Deliver(ImageLoad, data);
    internal bool DeliverImageUnload(ImageLoadTraceData data) => Deliver(ImageUnload, data);
    internal bool DeliverPerfInfoISR(ISRTraceData data) => Deliver(PerfInfoISR, data);
    internal bool DeliverPerfInfoDPC(DPCTraceData data) => Deliver(PerfInfoDPC, data);
    internal bool DeliverPerfInfoThreadedDPC(DPCTraceData data) => Deliver(PerfInfoThreadedDPC, data);
    internal bool DeliverPerfInfoTimerDPC(DPCTraceData data) => Deliver(PerfInfoTimerDPC, data);
    private static bool Deliver<T>(Action<T>? handler, T data)
    {
        if (handler is null) return false;
        handler(data);
        return true;
    }
}
