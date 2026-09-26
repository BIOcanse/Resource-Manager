using ResourceManager.App.Application.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader
{
    private sealed class MutableNetworkAttribution
    {
        public double ReceiveBytesPerSecond { get; set; }
        public double SendBytesPerSecond { get; set; }
        public int TcpConnectionCount { get; set; }
        public int UdpSocketCount { get; set; }
        public double EstimateWeight { get; set; }
        public bool IsEstimated { get; set; }
        public bool IsKnownProxy { get; set; }
        public int ActivityEventCount { get; set; }
        public double TotalBytesPerSecond => ReceiveBytesPerSecond + SendBytesPerSecond;

        public NetworkProcessAttribution ToSnapshot(int processId)
        {
            return new NetworkProcessAttribution(
                processId,
                Math.Max(0, ReceiveBytesPerSecond),
                Math.Max(0, SendBytesPerSecond),
                TcpConnectionCount,
                UdpSocketCount,
                IsEstimated,
                IsKnownProxy,
                ActivityEventCount);
        }
    }

    private sealed record NetworkEndpoint(
        string Key,
        int ProcessId,
        string Protocol,
        double EstimateWeight,
        MibTcpRow? Tcp4Row,
        bool IsHostLocal,
        bool LeavesHost,
        int LocalPort,
        int RemotePort)
    {
        public static NetworkEndpoint Tcp(
            string key,
            int processId,
            double estimateWeight,
            MibTcpRow? tcp4Row,
            bool isHostLocal,
            bool leavesHost,
            int localPort,
            int remotePort)
        {
            return new NetworkEndpoint(
                key,
                processId,
                NetworkEndpointProtocols.Tcp,
                estimateWeight,
                tcp4Row,
                isHostLocal,
                leavesHost,
                localPort,
                remotePort);
        }

        public static NetworkEndpoint Udp(
            string key,
            int processId,
            bool isHostLocal,
            bool leavesHost,
            int localPort)
        {
            return new NetworkEndpoint(
                key,
                processId,
                NetworkEndpointProtocols.Udp,
                1,
                null,
                isHostLocal,
                leavesHost,
                localPort,
                0);
        }
    }

    private sealed record ObservedNetworkEndpoint(
        NetworkEndpoint Endpoint,
        double ReceiveBytesPerSecond,
        double SendBytesPerSecond,
        bool HasExactBytes,
        bool IsKnownProxy)
    {
        public double Weight => Math.Max(
            Endpoint.EstimateWeight,
            HasExactBytes ? ReceiveBytesPerSecond + SendBytesPerSecond : 0);
    }

    private static class NetworkEndpointProtocols
    {
        public const string Tcp = "tcp";
        public const string Udp = "udp";
    }

    private sealed record TcpByteCounter(
        ulong ReceiveBytes,
        ulong SendBytes,
        DateTimeOffset ObservedAt);
}
