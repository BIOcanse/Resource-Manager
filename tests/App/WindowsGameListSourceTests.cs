using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.ProcessAttribution;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGameListSourceTests
{
    [Theory]
    [InlineData("entry")]
    [InlineData("user")]
    [InlineData("hives")]
    public void UnchangedRegistryFailuresWarnOnceAndRecoveryOrChangedFailureWarnsAgain(string scope)
    {
        var logger = new RecordingLogger();
        const string sid = "S-1-5-21-1-2-3-1001";
        var denied = true;
        var message = "denied";
        var source = new WindowsGameListSource(logger,
            path =>
            {
                if (denied && (scope == "hives" && path.Length == 0 || scope == "user" && path.Length > 0))
                    throw new UnauthorizedAccessException(message);
                return path.Length == 0 ? [sid, sid + "_Classes", "S-1-5-18"] : ["bad", "good", "empty"];
            },
            path =>
            {
                Assert.StartsWith(sid + @"\System\GameConfigStore\Children\", path);
                if (denied && scope == "entry" && path.EndsWith(@"\bad"))
                    throw new UnauthorizedAccessException(message);
                return path.EndsWith(@"\empty") ? " " : @"C:\Games\App.exe";
            });

        source.ReadExecutablePaths();
        source.ReadExecutablePaths();
        Assert.Single(logger.Warnings);
        message = "changed denial";
        source.ReadExecutablePaths();
        Assert.Equal(2, logger.Warnings.Count);
        denied = false;
        Assert.Equal(2, source.ReadExecutablePaths().Count);
        denied = true;
        source.ReadExecutablePaths();
        Assert.Equal(3, logger.Warnings.Count);
    }

    private sealed class RecordingLogger : ILogger<WindowsGameListSource>
    {
        internal List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, error));
        }
    }
}
