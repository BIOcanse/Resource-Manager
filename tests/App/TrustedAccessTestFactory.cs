using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Infrastructure.Security.TrustedAccess;

namespace Resource_Manager_APP.Tests;

internal static class TrustedAccessTestFactory
{
    public static (TrustedAccessKeyService Owner, TrustedAccessTestStore Store, TrustedAccessTestKeyFileAccess KeyFile) Create()
    {
        var result = CreateUninitialized();
        result.Owner.InitializeAsync(
            recover: false,
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public static (TrustedAccessKeyService Owner, TrustedAccessTestStore Store, TrustedAccessTestKeyFileAccess KeyFile) CreateUninitialized()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), $"trusted-access-test-{Guid.NewGuid():N}");
        var store = new TrustedAccessTestStore(packageRoot);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        return (new TrustedAccessKeyService(store, keyFile), store, keyFile);
    }
}

internal sealed class TrustedAccessTestStore(string packageRoot) : ITrustedAccessKeyStore
{
    private readonly object sync = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private event Action<Exception?>? Changed;
    private TrustedAccessKeyStoreState? state;
    private IReadOnlyList<StoredTrustedAccessKey> legacyAiCredentials = [];

    public string PackageRoot { get; } = packageRoot;

    public bool IsRevisionCurrent(string revision)
    {
        lock (sync) return state?.Revision == revision;
    }

    // The in-memory fixture has no separate committed revision publication.
    public void InvalidateRevision() { }

    public async Task<IDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken, bool ensureInitialized = false)
    {
        await mutationLock.WaitAsync(cancellationToken);
        return new Subscription(() => mutationLock.Release());
    }

    public IDisposable SubscribeToChanges(Action<Exception?> changed)
    {
        Changed += changed;
        return new Subscription(() => Changed -= changed);
    }

    public void NotifyFailure(Exception error) => Changed?.Invoke(error);

    public bool DatabaseExists
    {
        get
        {
            lock (sync)
            {
                return state is not null;
            }
        }
    }

    public Exception? LoadFailure { get; set; }

    public Exception? SaveFailure { get; set; }

    public bool SuppressChangeNotifications { get; set; }

    public int LegacyReadCount { get; private set; }

    public IReadOnlyList<StoredTrustedAccessKey> LegacyAiCredentials
    {
        get
        {
            lock (sync)
            {
                return CloneKeys(legacyAiCredentials);
            }
        }
        set
        {
            lock (sync)
            {
                legacyAiCredentials = CloneKeys(value);
            }
        }
    }

    public TrustedAccessKeyStoreState? CurrentState
    {
        get
        {
            lock (sync)
            {
                return state is null ? null : CloneState(state);
            }
        }
    }

    public Task<TrustedAccessKeyStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadFailure = LoadFailure;
        if (loadFailure is not null)
        {
            throw loadFailure;
        }

        lock (sync)
        {
            return Task.FromResult(state is null
                ? throw new FileNotFoundException("Test trusted access store is not initialized.")
                : CloneState(state));
        }
    }

    public Task InitializeAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (state is not null)
            {
                throw new IOException("Test trusted access store already exists.");
            }

            state = CloneState(initialState);
        }

        return Task.CompletedTask;
    }

    public Task SaveAsync(TrustedAccessKeyStoreState updatedState, string expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SaveFailure is not null) throw SaveFailure;
        lock (sync)
        {
            if (state is null)
            {
                throw new FileNotFoundException("Test trusted access store is not initialized.");
            }

            if (state.Revision != expectedRevision)
                throw new InvalidOperationException("Stale test store update.");

            state = CloneState(updatedState);
        }

        if (!SuppressChangeNotifications) Changed?.Invoke(null);

        return Task.CompletedTask;
    }

    public Task RecoverAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            state = CloneState(initialState);
        }

        if (!SuppressChangeNotifications) Changed?.Invoke(null);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredTrustedAccessKey>> ReadLegacyAiCredentialsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            LegacyReadCount++;
            return Task.FromResult(CloneKeys(legacyAiCredentials));
        }
    }

    private static TrustedAccessKeyStoreState CloneState(TrustedAccessKeyStoreState source) =>
        source with { Keys = CloneKeys(source.Keys) };

    private static IReadOnlyList<StoredTrustedAccessKey> CloneKeys(IEnumerable<StoredTrustedAccessKey> source) =>
        source.Select(static item => item with
        {
            Scopes = [.. item.Scopes],
            SecretHash = item.SecretHash.ToArray()
        }).ToArray();

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? action = dispose;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

internal sealed class TrustedAccessTestKeyFileAccess : ITrustedAccessKeyFileAccess
{
    public string? Value { get; private set; }

    public void ReplaceExternallyForTest(string? value) => Value = value;

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public Exception? WriteAfterReplacementFailureOnce { get; set; }

    public Exception? SubsequentWriteFailure { get; set; }

    public string? Read()
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Value;
    }

    public void WriteAtomic(string key)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Value = key;
        if (WriteAfterReplacementFailureOnce is { } failure)
        {
            WriteAfterReplacementFailureOnce = null;
            WriteFailure = SubsequentWriteFailure;
            throw failure;
        }
    }
}
