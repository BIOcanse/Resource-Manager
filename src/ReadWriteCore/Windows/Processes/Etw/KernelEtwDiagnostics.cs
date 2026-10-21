namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public enum KernelEtwDiagnosticLevel { Debug, Information, Warning, Error }
public sealed record KernelEtwDiagnostic(KernelEtwDiagnosticLevel Level, Exception? Exception,
    string MessageTemplate, IReadOnlyList<object?> Arguments);

public sealed class KernelEtwDiagnostics(Action<KernelEtwDiagnostic>? report = null)
{
    private void Emit(KernelEtwDiagnosticLevel level, Exception? exception, string message, object?[] arguments)
        => report?.Invoke(new(level, exception, message, arguments));
    internal void LogDebug(string message, params object?[] args) => Emit(KernelEtwDiagnosticLevel.Debug, null, message, args);
    internal void LogDebug(Exception ex, string message, params object?[] args) => Emit(KernelEtwDiagnosticLevel.Debug, ex, message, args);
    internal void LogWarning(Exception ex, string message, params object?[] args) => Emit(KernelEtwDiagnosticLevel.Warning, ex, message, args);
    internal void LogError(string message, params object?[] args) => Emit(KernelEtwDiagnosticLevel.Error, null, message, args);
    internal void LogError(Exception ex, string message, params object?[] args) => Emit(KernelEtwDiagnosticLevel.Error, ex, message, args);
}
