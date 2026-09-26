using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Application.Migration;

public interface ISoftwareDataDiscoveryManager
{
    IReadOnlyList<SoftwareDataDiscoveryCandidate> FindNameCandidates(SoftwareDataDiscoveryRequest request);

    IReadOnlyList<SoftwareDataDiscoverySession> GetSessions();

    Task<SoftwareDataDiscoverySession> StartSessionAsync(
        SoftwareDataDiscoveryStartRequest request,
        CancellationToken cancellationToken);

    SoftwareDataDiscoverySession? StopSession(string id);
}
