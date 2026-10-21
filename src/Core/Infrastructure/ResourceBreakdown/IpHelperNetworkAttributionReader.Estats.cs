using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader
{
    private bool TryReadTcpBytes(
        string key,
        MibTcpRow tcpRow,
        DateTimeOffset now,
        Dictionary<string, TcpByteCounter> currentTcpBytes,
        out double receiveBytesPerSecond,
        out double sendBytesPerSecond)
    {
        receiveBytesPerSecond = 0;
        sendBytesPerSecond = 0;

        if (tcpRow.State != TcpStateEstablished || !tcpDataStats.Read(tcpRow, out var enabled, out var data))
        {
            return false;
        }
        if (!enabled)
        {
            if (!tcpDataStats.SetEnabled(tcpRow, true)) return false;
            enabledTcpStats[key] = tcpRow;
            if (!tcpDataStats.Read(tcpRow, out enabled, out data) || !enabled) return false;
        }

        var current = new TcpByteCounter(data.DataBytesIn, data.DataBytesOut, now);
        currentTcpBytes[key] = current;
        if (!previousTcpBytes.TryGetValue(key, out var previous))
        {
            return true;
        }

        var elapsedSeconds = Math.Max(0.001, (now - previous.ObservedAt).TotalSeconds);
        if (current.ReceiveBytes >= previous.ReceiveBytes)
        {
            receiveBytesPerSecond = (current.ReceiveBytes - previous.ReceiveBytes) / elapsedSeconds;
        }

        if (current.SendBytes >= previous.SendBytes)
        {
            sendBytesPerSecond = (current.SendBytes - previous.SendBytes) / elapsedSeconds;
        }

        return true;
    }

    private void ReleaseTcpStats()
    {
        foreach (var (key, row) in enabledTcpStats.ToArray())
        {
            if (!tcpDataStats.Read(row, out var enabled, out _) || !enabled || tcpDataStats.SetEnabled(row, false))
                enabledTcpStats.Remove(key);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            ReleaseTcpStats();
            previousTcpBytes.Clear();
            disposed = true;
        }
    }

    internal interface ITcpDataStats
    {
        bool Read(MibTcpRow row, out bool enabled, out TcpEstatsDataRodV0 data);
        bool SetEnabled(MibTcpRow row, bool enabled);
    }

    private sealed class NativeTcpDataStats : ITcpDataStats
    {
        public bool Read(MibTcpRow row, out bool enabled, out TcpEstatsDataRodV0 data)
        {
            var result = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, out var config, 0,
                (uint)Marshal.SizeOf<TcpEstatsDataRwV0>(), IntPtr.Zero, 0, 0, out data, 0,
                (uint)Marshal.SizeOf<TcpEstatsDataRodV0>());
            enabled = result == ErrorSuccess && config.EnableCollection != 0;
            if (!enabled) data = default;
            return result == ErrorSuccess;
        }
        public bool SetEnabled(MibTcpRow row, bool enabled)
        {
            var config = new TcpEstatsDataRwV0 { EnableCollection = enabled ? (byte)1 : (byte)0 };
            return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, ref config, 0,
                (uint)Marshal.SizeOf<TcpEstatsDataRwV0>(), 0) == ErrorSuccess;
        }
    }
}
