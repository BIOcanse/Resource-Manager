using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Software;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Application.LocalSystem;

public interface ISystemProcessActionService
{
    LocalOnlineSearchResult SearchOnline(LocalOnlineSearchRequest request, SoftwareOperationCaller caller);

    LocalPathOpenResult OpenProperties(LocalPathPropertiesRequest request, SoftwareOperationCaller caller);

    SystemProcessOperationResult TerminateProcesses(SystemProcessOperationRequest request, SafeAccessTokenHandle callerToken);

    SystemProcessOperationResult CreateProcessDumps(SystemProcessOperationRequest request, SafeAccessTokenHandle callerToken);
}
