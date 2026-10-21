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

    internal void Dispatch<T>(T data, Action<KernelEtwEventSource, T> deliver)
    {
        // This read admits the synchronous delivery. Cancellation prevents later
        // admissions; a delivery already admitted may finish without a blocking wait.
        if (IsActive) deliver(this, data);
    }

    internal void DeliverProcessStartGroup(ProcessTraceData data) => ProcessStartGroup?.Invoke(data);
    internal void DeliverProcessEndGroup(ProcessTraceData data) => ProcessEndGroup?.Invoke(data);
    internal void DeliverThreadStartGroup(ThreadTraceData data) => ThreadStartGroup?.Invoke(data);
    internal void DeliverThreadEndGroup(ThreadTraceData data) => ThreadEndGroup?.Invoke(data);
    internal void DeliverThreadCSwitch(CSwitchTraceData data) => ThreadCSwitch?.Invoke(data);
    internal void DeliverDiskIORead(DiskIOTraceData data) => DiskIORead?.Invoke(data);
    internal void DeliverDiskIOWrite(DiskIOTraceData data) => DiskIOWrite?.Invoke(data);
    internal void DeliverFileIOCreate(FileIOCreateTraceData data) => FileIOCreate?.Invoke(data);
    internal void DeliverFileIOFileCreate(FileIONameTraceData data) => FileIOFileCreate?.Invoke(data);
    internal void DeliverFileIOFileDelete(FileIONameTraceData data) => FileIOFileDelete?.Invoke(data);
    internal void DeliverFileIOWrite(FileIOReadWriteTraceData data) => FileIOWrite?.Invoke(data);
    internal void DeliverFileIORename(FileIOInfoTraceData data) => FileIORename?.Invoke(data);
    internal void DeliverFileIODelete(FileIOInfoTraceData data) => FileIODelete?.Invoke(data);
    internal void DeliverTcpIpSend(TcpIpSendTraceData data) => TcpIpSend?.Invoke(data);
    internal void DeliverTcpIpRecv(TcpIpTraceData data) => TcpIpRecv?.Invoke(data);
    internal void DeliverTcpIpSendIPV6(TcpIpV6SendTraceData data) => TcpIpSendIPV6?.Invoke(data);
    internal void DeliverTcpIpRecvIPV6(TcpIpV6TraceData data) => TcpIpRecvIPV6?.Invoke(data);
    internal void DeliverUdpIpSend(UdpIpTraceData data) => UdpIpSend?.Invoke(data);
    internal void DeliverUdpIpRecv(UdpIpTraceData data) => UdpIpRecv?.Invoke(data);
    internal void DeliverUdpIpSendIPV6(UpdIpV6TraceData data) => UdpIpSendIPV6?.Invoke(data);
    internal void DeliverUdpIpRecvIPV6(UpdIpV6TraceData data) => UdpIpRecvIPV6?.Invoke(data);
    internal void DeliverImageDCStart(ImageLoadTraceData data) => ImageDCStart?.Invoke(data);
    internal void DeliverImageLoad(ImageLoadTraceData data) => ImageLoad?.Invoke(data);
    internal void DeliverImageUnload(ImageLoadTraceData data) => ImageUnload?.Invoke(data);
    internal void DeliverPerfInfoISR(ISRTraceData data) => PerfInfoISR?.Invoke(data);
    internal void DeliverPerfInfoDPC(DPCTraceData data) => PerfInfoDPC?.Invoke(data);
    internal void DeliverPerfInfoThreadedDPC(DPCTraceData data) => PerfInfoThreadedDPC?.Invoke(data);
    internal void DeliverPerfInfoTimerDPC(DPCTraceData data) => PerfInfoTimerDPC?.Invoke(data);
}
