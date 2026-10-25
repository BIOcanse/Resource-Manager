namespace ResourceManager.App.Application.TrustedAccess;

public interface ITrustedAccessKeyFileAccess
{
    string? Read();

    void WriteAtomic(string key);
}
