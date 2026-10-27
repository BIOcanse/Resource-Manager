using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwConsumerThreadTests
{
    [Fact]
    public async Task NamedConsumerThreadStaysRegisteredUntilItsActionCompletes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registrationStarted = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        using var allowDispose = new ManualResetEventSlim();
        using var registrationDisposed = new ManualResetEventSlim();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var observation = new ThreadObservation(
            registrationStarted,
            disposalStarted,
            registrationDisposed,
            allowDispose);
        var actionThreadId = 0;
        string? actionThreadName = null;
        var iterations = 0;

        var processing = DedicatedEtwConsumerThread.Start(
            101,
            "session-1",
            () =>
            {
                Interlocked.Exchange(ref actionThreadId, Environment.CurrentManagedThreadId);
                actionThreadName = Thread.CurrentThread.Name;
                entered.Set();
                while (!release.IsSet)
                {
                    Interlocked.Increment(ref iterations);
                    Thread.SpinWait(256);
                }
            },
            out var consumerThread,
            observation);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(registrationStarted.IsSet);
            Assert.False(registrationDisposed.IsSet);
            Assert.Equal(101, observation.BlockId);
            Assert.Equal("session-1", observation.SessionName);
            Assert.Equal("101 / session-1", actionThreadName);
            Assert.NotEqual(callerThreadId, Volatile.Read(ref actionThreadId));
            Assert.NotEqual(callerThreadId, observation.ManagedThreadId);
            Assert.Equal(observation.ManagedThreadId, Volatile.Read(ref actionThreadId));

            release.Set();
            Assert.True(disposalStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            release.Set();
            allowDispose.Set();
        }

        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(consumerThread.Join(TimeSpan.FromSeconds(10)));
        Assert.False(consumerThread.IsAlive);

        Assert.True(registrationDisposed.IsSet);
        Assert.True(Volatile.Read(ref iterations) > 0);
    }

    [Fact]
    public async Task ConsumerFailureFaultsTheReturnedTaskAndReleasesRegistration()
    {
        using var registrationStarted = new ManualResetEventSlim();
        using var registrationDisposed = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        var observation = new ThreadObservation(
            registrationStarted,
            disposalStarted,
            registrationDisposed);
        var processing = DedicatedEtwConsumerThread.Start(
            102,
            "session-2",
            static () => throw new InvalidOperationException("consumer failure"),
            observation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("consumer failure", error.Message);
        Assert.True(registrationStarted.IsSet);
        Assert.True(registrationDisposed.IsSet);
        var reported = Assert.Single(observation.ReportedFailures);
        Assert.Equal(102, reported.BlockId);
        Assert.Same(error, reported.Error);
    }

    [Fact]
    public async Task FailureReportErrorIsAggregatedWithTheConsumerFailure()
    {
        using var registrationStarted = new ManualResetEventSlim();
        using var registrationDisposed = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        var consumerError = new InvalidOperationException("consumer failure");
        var reportingError = new InvalidOperationException("report failure");
        var observation = new ThreadObservation(
            registrationStarted,
            disposalStarted,
            registrationDisposed,
            reportFailureError: reportingError);
        var processing = DedicatedEtwConsumerThread.Start(
            102,
            "session-3",
            () => throw consumerError,
            observation);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Collection(error.InnerExceptions,
            original => Assert.Same(consumerError, original),
            reportError => Assert.Same(reportingError, reportError));
        Assert.True(registrationDisposed.IsSet);
    }

    private sealed class ThreadObservation(
        ManualResetEventSlim registrationStarted,
        ManualResetEventSlim disposalStarted,
        ManualResetEventSlim registrationDisposed,
        ManualResetEventSlim? allowDispose = null,
        Exception? reportFailureError = null) : IEtwConsumerThreadObserver
    {
        public List<(int BlockId, Exception Error)> ReportedFailures { get; } = [];
        public int? BlockId { get; private set; }
        public string? SessionName { get; private set; }
        public int ManagedThreadId { get; private set; }

        public IDisposable RegisterCurrentThread(int blockId, string sessionName)
        {
            BlockId = blockId;
            SessionName = sessionName;
            ManagedThreadId = Environment.CurrentManagedThreadId;
            registrationStarted.Set();
            return new Registration(disposalStarted, registrationDisposed, allowDispose);
        }

        public void ReportFailure(int blockId, Exception error)
        {
            ReportedFailures.Add((blockId, error));
            if (reportFailureError is not null)
            {
                throw reportFailureError;
            }
        }
    }

    private sealed class Registration(
        ManualResetEventSlim disposalStarted,
        ManualResetEventSlim registrationDisposed,
        ManualResetEventSlim? allowDispose) : IDisposable
    {
        public void Dispose()
        {
            disposalStarted.Set();
            allowDispose?.Wait();
            registrationDisposed.Set();
        }
    }
}
