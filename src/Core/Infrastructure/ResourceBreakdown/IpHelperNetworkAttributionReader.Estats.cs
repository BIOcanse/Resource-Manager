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

        if (tcpRow.State != TcpStateEstablished || !TryEnableTcpDataStats(ref tcpRow))
        {
            return false;
        }

        if (!TryGetTcpDataStats(ref tcpRow, out var data))
        {
            return false;
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

    private static bool TryEnableTcpDataStats(ref MibTcpRow row)
    {
        var config = new TcpEstatsDataRwV0 { EnableCollection = 1 };
        return SetPerTcpConnectionEStats(
            ref row,
            TcpConnectionEstatsData,
            ref config,
            0,
            (uint)Marshal.SizeOf<TcpEstatsDataRwV0>(),
            0) == ErrorSuccess;
    }

    private static bool TryGetTcpDataStats(ref MibTcpRow row, out TcpEstatsDataRodV0 data)
    {
        return GetPerTcpConnectionEStats(
            ref row,
            TcpConnectionEstatsData,
            IntPtr.Zero,
            0,
            0,
            IntPtr.Zero,
            0,
            0,
            out data,
            0,
            (uint)Marshal.SizeOf<TcpEstatsDataRodV0>()) == ErrorSuccess;
    }
}
