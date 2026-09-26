namespace ResourceManager.App.Application.Diagnostics;

public interface IDebugDiagnosticLogReader
{
    IReadOnlyList<string> ReadTail(int requestedLines);
}
