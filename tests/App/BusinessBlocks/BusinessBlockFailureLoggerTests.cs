using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockFailureLoggerTests
{
    [Fact]
    public void LegacyFailureIsReportedEvenWithLoggingDisabledAndOriginalLogIsForwarded()
    {
        var inner = new ProbeLogger();
        var diagnostics = new ProbeDiagnostics();
        var logger = new BusinessBlockFailureLogger<object>(inner, diagnostics, 101);
        var error = new IOException("caught failure");

        Assert.True(logger.IsEnabled(LogLevel.Warning));
        logger.LogWarning(error, "Original failure {Value}", 42);
        logger.LogWarning("An ordinary warning");

        Assert.Equal(2, inner.Logs);
        Assert.Same(error, diagnostics.Error);
        Assert.Equal(101, diagnostics.BlockId);
        Assert.Equal(1, diagnostics.Failures);
    }

    private sealed class ProbeLogger : ILogger<object>
    {
        public int Logs;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Logs++;
    }

    private sealed class ProbeDiagnostics : IBusinessBlockDiagnostics
    {
        public Exception? Error;
        public int? BlockId;
        public int Failures;
        public void ReportFailure(int blockId, Exception error, string operation)
        {
            Error = error;
            BlockId = blockId;
            Failures++;
        }
        public IDisposable MeasureSegment(int blockId) => throw new NotSupportedException();
    }
}
