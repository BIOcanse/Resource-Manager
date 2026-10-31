using Microsoft.Extensions.Logging;

namespace ResourceManager.NativeUi;

internal sealed class NativeUiTraceLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => System.Diagnostics.Trace.WriteLine($"{typeof(T).Name}: {formatter(state, exception)}");
}
