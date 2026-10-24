using Microsoft.Extensions.Logging;

namespace Resource_Manager_APP.Tests;

internal sealed class SettingsBackupWarningLogger<T> : ILogger<T>
{
    internal List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception, formatter(state, exception)));
}
