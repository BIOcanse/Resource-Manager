using ResourceManager.App.Application.Diagnostics;

namespace ResourceManager.App.Infrastructure.Diagnostics;

public sealed class JsonDebugDiagnosticLogReader(IHostEnvironment environment) : IDebugDiagnosticLogReader
{
    private readonly string logPath = DebugDiagnosticLogStorage.ResolvePath(environment.ContentRootPath);

    public IReadOnlyList<string> ReadTail(int requestedLines)
    {
        return DebugDiagnosticLogStorage.ReadTail(logPath, requestedLines);
    }
}
