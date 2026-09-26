using ResourceManager.App.Domain.LocalSystem;

namespace ResourceManager.App.Application.LocalSystem;

public interface ISystemProcessActionService
{
    LocalOnlineSearchResult SearchOnline(LocalOnlineSearchRequest request);

    LocalPathOpenResult OpenProperties(LocalPathPropertiesRequest request);

    SystemProcessOperationResult TerminateProcesses(SystemProcessOperationRequest request);

    SystemProcessOperationResult CreateProcessDumps(SystemProcessOperationRequest request);
}
