namespace ResourceManager.App.Infrastructure.Security;

public interface ILoopbackApiAdministratorReader
{
    bool IsAdministrator(HttpContext context);
}
