using System.Globalization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class EtwNetworkTcpIpCaptureOwner
{
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess =
        WindowsNetworkEndpointProcessLease.Open;

    internal EtwNetworkTcpIpCaptureOwner(IKernelEtwSessionBroker broker, Func<bool> canRead,
        KernelEtwDiagnostics diagnostics, Func<long> clock, Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess)
        : this(broker, canRead, diagnostics, clock)
    {
        this.openProcess = openProcess ?? throw new ArgumentNullException(nameof(openProcess));
    }

    private ProcessNetworkTrafficWindow BuildObservedTraffic(NetworkTransferEvent[] selected, DateTimeOffset capturedAt)
    {
        var rows = new List<ProcessNetworkTrafficObservation>();
        var leases = new List<(IWindowsNetworkEndpointProcessLease Lease, ulong Creation, int Row)>();
        try
        {
            foreach (var group in selected.GroupBy(static item => item.ProcessId).OrderBy(static group => group.Key))
            {
                var rowIndex = rows.Count;
                rows.Add(UnavailableProcess(group.Key));
                IWindowsNetworkEndpointProcessLease? lease = null;
                try
                {
                    lease = openProcess(checked((uint)group.Key));
                    if (lease is null) continue;
                    leases.Add((lease, 0, rowIndex)); // Every opened handle is released, including failed reads.
                    if (lease.ProcessId != (uint)group.Key || !lease.TryReadCreationFileTime(out var creation)
                        || creation == 0 || group.Any(static item => item.EventWindowsFileTime100Ns is null)) continue;
                    ulong receive = 0, send = 0;
                    foreach (var item in group)
                    {
                        // The PID may now belong to a process created after this queued event.
                        if (item.EventWindowsFileTime100Ns < creation) continue;
                        receive = checked(receive + item.ReceiveBytes);
                        send = checked(send + item.SendBytes);
                    }
                    rows[rowIndex] = new(group.Key, creation.ToString(CultureInfo.InvariantCulture),
                        receive.ToString(CultureInfo.InvariantCulture), send.ToString(CultureInfo.InvariantCulture),
                        receive / CalculationWindow.TotalSeconds, send / CalculationWindow.TotalSeconds);
                    leases[^1] = (lease, creation, rowIndex);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not verify ETW network process identity for {ProcessId}.", group.Key);
                }
            }
            foreach (var (lease, creation, row) in leases)
            {
                if (creation == 0) continue;
                try
                {
                    if (!lease.TryReadCreationFileTime(out var finalCreation) || finalCreation != creation)
                        rows[row] = UnavailableProcess(rows[row].ProcessId);
                }
                catch (Exception ex)
                {
                    rows[row] = UnavailableProcess(rows[row].ProcessId);
                    logger.LogDebug(ex, "Could not recheck ETW network process identity.");
                }
            }
            return new(capturedAt, CalculationWindowMilliseconds, rows.ToArray());
        }
        finally
        {
            foreach (var (lease, _, _) in leases)
            {
                try { lease.Dispose(); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not release ETW network process identity handle."); }
            }
        }
    }

    private static ProcessNetworkTrafficObservation UnavailableProcess(int processId)
        => new(processId, null, null, null, null, null);

    internal static ulong? ToEventFileTime(DateTime timestamp)
    {
        try { return checked((ulong)timestamp.ToUniversalTime().ToFileTimeUtc()); }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }
}
