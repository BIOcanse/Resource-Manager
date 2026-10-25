using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>
/// Registration-layer observation for a legacy block that reports caught failures through ILogger.
/// It forwards the original log and reports exception-bearing warnings/errors, without rethrowing
/// or changing the worker's continuation. Used only for the launch reconciler in step 1.
/// </summary>
internal sealed class BusinessBlockFailureLogger<T>(ILogger<T> inner, IBusinessBlockDiagnostics diagnostics, string blockId) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning || inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);
        if (logLevel >= LogLevel.Warning && exception is not null)
            diagnostics.ReportFailure(blockId, exception, "logged-exception");
    }
}
