using System.IO.Pipes;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.Security;

public sealed partial class FrontendSessionCredentialHostedService
{
    private async Task RespondToLauncherAsync(NamedPipeServerStream pipe, int expectedSession,
        string expectedSid, string expectedFrontend, CancellationToken cancellationToken)
    {
        // The service and managed Launcher are sibling product binaries. Never
        // accept an executable path, session or SID supplied by the requesting peer.
        var launcher = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "ResourceManagerLauncher", "ResourceManager.Launcher.exe"));
        var caller = new WindowsNamedPipeCallerAttester().Attest(pipe, runtime.InstanceId,
            checked((ulong)Interlocked.Increment(ref nextConnection)));
        var background = await NativeUiOpenSessionContract.ReadRequestAsync(pipe, cancellationToken);
        var processId = NativeUiLaunchHostedService.OpenInterface(sessions, caller,
            checked((uint)expectedSession), expectedSid, launcher, expectedFrontend, background);
        logger.LogInformation("Explicitly opened standard-user Native UI in Windows session {SessionId}, PID {ProcessId}.",
            expectedSession, processId);
        await pipe.WriteAsync(new byte[] { NativeUiOpenSessionContract.Accepted }, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }
}
