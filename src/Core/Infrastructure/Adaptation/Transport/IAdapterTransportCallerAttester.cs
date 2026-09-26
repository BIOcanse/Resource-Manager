using System.IO.Pipes;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Adaptation.Transport;

internal interface IAdapterTransportCallerAttester
{
    TrustedAdapterCallerIdentity Attest(
        NamedPipeServerStream connectedPipe,
        ulong hostInstanceId,
        ulong transportConnectionId);
}
