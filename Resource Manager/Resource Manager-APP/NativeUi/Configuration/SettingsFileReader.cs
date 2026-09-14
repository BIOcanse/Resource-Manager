namespace ResourceManager.NativeUi.Configuration;

internal static class SettingsFileReader
{
    // A reader keeps its complete old image while the backend replaces the pathname.
    internal static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
}
