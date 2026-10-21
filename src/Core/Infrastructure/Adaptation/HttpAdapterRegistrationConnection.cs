using System.Net;
using System.Net.Sockets;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal static class HttpAdapterRegistrationConnection
{
    internal static readonly HttpRequestOptionsKey<AdapterSoftwareRegistrationRequest> RegistrationKey = new("AdapterRegistration");
    internal static readonly HttpRequestOptionsKey<AdapterRegistrationCaller> CallerKey = new("AdapterRegistrationCaller");
    internal static readonly HttpRequestOptionsKey<AdapterRegistrationOwnership> OwnershipKey = new("AdapterRegistrationOwnership");

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        // Each registration needs its own connected-row proof; pooled connections would skip ConnectCallback.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var request = context.InitialRequestMessage;
            if (!request.Options.TryGetValue(RegistrationKey, out var registration)
                || !request.Options.TryGetValue(CallerKey, out var caller))
                throw new UnauthorizedAccessException("Registration ownership inputs are unavailable.");
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            var address = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
                : IPAddress.TryParse(host, out var parsed) && IPAddress.IsLoopback(parsed) ? parsed
                : throw new UnauthorizedAccessException("Resource endpoints must connect directly to loopback.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                if (!WindowsLoopbackApiAdministratorReader.TryReadConnectedPeer(socket, out var owner))
                    throw new UnauthorizedAccessException("The connected resource endpoint owner is unavailable.");
                var ownership = AdapterRegistrationOwnership.Verify(registration, caller, owner, ReadProcess);
                if (!ownership.StillOwned(ReadProcess))
                    throw new UnauthorizedAccessException("The registration process identity changed.");
                request.Options.Set(OwnershipKey, ownership);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    internal static LoopbackApiProcess? ReadProcess(int processId)
        => WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(processId, out var identity) ? identity : null;
}
