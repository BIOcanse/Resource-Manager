using System.IO.Pipes;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.Security;

public sealed partial class FrontendSessionCredentialHostedService
{
    internal async Task HandleSessionRequestAsync(NamedPipeServerStream pipe, int expectedSession, string expectedSid,
        string expectedFrontend, string expectedBroker, CancellationToken cancellationToken)
    {
        var operation = new byte[1];
        await pipe.ReadExactlyAsync(operation, cancellationToken);
        if (operation[0] == NativeUiOpenSessionContract.OpenOperation)
        {
            await RespondToLauncherAsync(pipe, expectedSession, expectedSid, expectedFrontend, cancellationToken);
            return;
        }
        if (operation[0] == FrontendSessionCredentialContract.ProtocolVersion)
        {
            await RespondToFrontendAsync(pipe, expectedSession, expectedSid, expectedFrontend, cancellationToken);
            return;
        }
        if (operation[0] is not (GpuLaunchSessionContract.ResolveOperation or GpuLaunchSessionContract.ReportOperation))
            throw new InvalidDataException("Unsupported user session operation.");
        AttestProductCaller(pipe, expectedSession, expectedSid, expectedBroker);
        var body = await GpuLaunchSessionContract.ReadAsync(pipe, cancellationToken);
        var provider = services ?? throw new InvalidOperationException("GPU launch services are unavailable.");
        if (operation[0] == GpuLaunchSessionContract.ResolveOperation)
        {
            if (string.IsNullOrWhiteSpace(body) || body.Length > 32_767)
                throw new InvalidDataException("Invalid startup executable path.");
            var decision = await provider.GetRequiredService<IGpuStartupPlacementResolver>()
                .ResolveAsync(new GpuStartupPlacementRequest(body), cancellationToken);
            await GpuLaunchSessionContract.WriteAsync(pipe, GpuLaunchBrokerProtocol.Serialize(decision), cancellationToken);
        }
        else
        {
            if (!GpuLaunchBrokerProtocol.TryParseExecutionReport(body, out var report, out _) || report is null)
                throw new InvalidDataException("Invalid GPU launch report.");
            await provider.GetRequiredService<IGpuLaunchExecutionReportStore>().RecordAsync(report, cancellationToken);
            await GpuLaunchSessionContract.WriteAsync(pipe, "ok", cancellationToken);
        }
    }
}
