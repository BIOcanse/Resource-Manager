namespace ResourceManager.App.Infrastructure.Migration.Etw;

public interface IFirstRunFileTrace : IDisposable
{
    FirstRunTraceStartResult Start(FirstRunTraceOptions options, Action<FileTraceEventRecord> observe);

    void Stop();
}

public interface IFirstRunFileTraceFactory
{
    IFirstRunFileTrace Create();
}

public sealed record FirstRunTraceOptions(
    string SessionId,
    IReadOnlyList<string> RootPaths);

public sealed record FirstRunTraceStartResult(
    bool Started,
    string Provider,
    string State,
    string Message);

public sealed record FileTraceEventRecord(
    string Provider,
    string EventType,
    string? Path,
    int ProcessId,
    string ProcessName,
    int? ParentProcessId,
    long? SizeBytes,
    DateTimeOffset ObservedAt);
