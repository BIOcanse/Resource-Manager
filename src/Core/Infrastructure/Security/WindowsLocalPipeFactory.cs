using System.IO.Pipes;

namespace ResourceManager.App.Infrastructure.Security;

internal static class WindowsLocalPipeFactory
{
    internal static NamedPipeServerStream Create(string pipeName, string protectedDaclSddl,
        int inputBufferSize, int outputBufferSize)
        => ResourceManager.Shared.Runtime.WindowsSessionPipeFactory.Create(
            pipeName, protectedDaclSddl, inputBufferSize, outputBufferSize);
}
