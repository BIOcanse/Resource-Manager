using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.LocalSystem;

public interface ILocalPathOpener
{
    LocalPathOpenResult Open(LocalPathOpenRequest request, SoftwareOperationCaller? caller);
}
