namespace ResourceManager.App.Application.ProcessAttribution;

public interface IWindowsGameListSource
{
    IReadOnlyList<string> ReadExecutablePaths();
}
