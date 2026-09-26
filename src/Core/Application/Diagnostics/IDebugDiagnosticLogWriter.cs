using ResourceManager.App.Domain.Diagnostics;

namespace ResourceManager.App.Application.Diagnostics;

public interface IDebugDiagnosticLogWriter
{
    bool TryWrite(DebugDiagnosticLogRecord record);
}
