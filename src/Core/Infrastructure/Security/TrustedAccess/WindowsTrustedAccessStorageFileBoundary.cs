using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security.TrustedAccess;

public sealed class WindowsTrustedAccessStorageFileBoundary : ITrustedAccessStorageFileBoundary
{
    public bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public void EnsureSecurityDirectory(string packageRoot) =>
        TrustedAccessKeyFile.EnsureSecurityDirectoryForPackage(packageRoot);

    public void VerifySecurityDirectory(string packageRoot) =>
        TrustedAccessKeyFile.VerifySecurityDirectoryForPackage(packageRoot);

    public void CreatePrivateFile(string path) => TrustedAccessKeyFile.CreatePrivateFile(path);

    public void VerifyPrivateFile(string path) => TrustedAccessKeyFile.VerifyPrivateFile(path);

    public void DeletePrivateFile(string path)
    {
        if (!Exists(path))
        {
            return;
        }

        VerifyPrivateFile(path);
        File.Delete(path);
    }
}
