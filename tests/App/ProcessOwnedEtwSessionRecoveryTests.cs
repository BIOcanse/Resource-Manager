using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessOwnedEtwSessionRecoveryTests
{
    private const string Prefix = "ResourceManagerKernelTelemetry";

    [Fact]
    public void SessionName_ParsesModernAndLegacyCanonicalIdentitiesOnly()
    {
        var modern = new ProcessOwnedEtwSessionOwner(42, 638916000000000000);
        var modernName = ResourceManagerEtwSessionNames.Create(Prefix, modern);

        Assert.True(ResourceManagerEtwSessionNames.TryParse(Prefix, modernName, out var parsedModern));
        Assert.Equal(modern, parsedModern);

        Assert.True(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}-42", out var parsedLegacy));
        Assert.Equal(new ProcessOwnedEtwSessionOwner(42, null), parsedLegacy);

        Assert.False(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}-042", out _));
        Assert.False(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}-42-0638916000000000000", out _));
        Assert.False(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}-42-638916000000000000-extra", out _));
        Assert.False(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}Extra-42-638916000000000000", out _));
        Assert.False(ResourceManagerEtwSessionNames.TryParse(Prefix, $"{Prefix}-0", out _));
    }

    [Fact]
    public void Reconcile_StopsOnlyTwiceConfirmedOrphans()
    {
        var current = new ProcessOwnedEtwSessionOwner(100, 1000);
        var currentName = ResourceManagerEtwSessionNames.Create(Prefix, current);
        var liveLegacy = new ProcessOwnedEtwSessionOwner(200, null);
        var deadLegacy = new ProcessOwnedEtwSessionOwner(201, null);
        var reusedModern = new ProcessOwnedEtwSessionOwner(202, 2000);
        var unknownModern = new ProcessOwnedEtwSessionOwner(203, 3000);
        var revivedDuringAttach = new ProcessOwnedEtwSessionOwner(204, 4000);
        var sessionNames = new[]
        {
            currentName,
            $"{Prefix}-200",
            $"{Prefix}-201",
            ResourceManagerEtwSessionNames.Create(Prefix, reusedModern),
            ResourceManagerEtwSessionNames.Create(Prefix, unknownModern),
            ResourceManagerEtwSessionNames.Create(Prefix, revivedDuringAttach),
            "UnrelatedSession-201",
            $"{Prefix}-0205",
            $"{Prefix}-205-5000-extra"
        };
        var catalog = new FakeSessionCatalog(sessionNames);
        var inspector = new FakeProcessInspector();
        inspector.Set(liveLegacy, ProcessOwnedEtwSessionOwnerState.Matching);
        inspector.Set(deadLegacy, ProcessOwnedEtwSessionOwnerState.Missing, ProcessOwnedEtwSessionOwnerState.Missing);
        inspector.Set(reusedModern, ProcessOwnedEtwSessionOwnerState.Different, ProcessOwnedEtwSessionOwnerState.Different);
        inspector.Set(unknownModern, ProcessOwnedEtwSessionOwnerState.Unknown);
        inspector.Set(
            revivedDuringAttach,
            ProcessOwnedEtwSessionOwnerState.Missing,
            ProcessOwnedEtwSessionOwnerState.Matching);
        var reconciler = new ProcessOwnedEtwSessionOrphanReconciler(
            catalog,
            inspector,
            NullLogger.Instance);

        var result = reconciler.Reconcile(Prefix, current, currentName);

        Assert.Equal(6, result.ProductSessionCount);
        Assert.Equal(3, result.OrphanCandidateCount);
        Assert.Equal(2, result.StoppedCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(
            [$"{Prefix}-201", ResourceManagerEtwSessionNames.Create(Prefix, reusedModern), ResourceManagerEtwSessionNames.Create(Prefix, revivedDuringAttach)],
            catalog.AttachedNames);
        Assert.Equal(
            [$"{Prefix}-201", ResourceManagerEtwSessionNames.Create(Prefix, reusedModern)],
            catalog.StoppedNames);
    }

    [Fact]
    public void Reconcile_StopFailureIsContained()
    {
        var current = new ProcessOwnedEtwSessionOwner(100, 1000);
        var stale = new ProcessOwnedEtwSessionOwner(300, 3000);
        var staleName = ResourceManagerEtwSessionNames.Create(Prefix, stale);
        var catalog = new FakeSessionCatalog([staleName]);
        catalog.StopFailures.Add(staleName);
        var inspector = new FakeProcessInspector();
        inspector.Set(stale, ProcessOwnedEtwSessionOwnerState.Missing, ProcessOwnedEtwSessionOwnerState.Missing);
        var reconciler = new ProcessOwnedEtwSessionOrphanReconciler(
            catalog,
            inspector,
            NullLogger.Instance);

        var result = reconciler.Reconcile(
            Prefix,
            current,
            ResourceManagerEtwSessionNames.Create(Prefix, current));

        Assert.Equal(1, result.OrphanCandidateCount);
        Assert.Equal(0, result.StoppedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal([staleName], catalog.AttachedNames);
    }

    [Fact]
    public void Reconcile_EnumerationFailureIsContained()
    {
        var current = new ProcessOwnedEtwSessionOwner(100, 1000);
        var catalog = new FakeSessionCatalog([])
        {
            EnumerationFailure = new InvalidOperationException("fixture")
        };
        var reconciler = new ProcessOwnedEtwSessionOrphanReconciler(
            catalog,
            new FakeProcessInspector(),
            NullLogger.Instance);

        var result = reconciler.Reconcile(
            Prefix,
            current,
            ResourceManagerEtwSessionNames.Create(Prefix, current));

        Assert.Equal(0, result.StoppedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Empty(catalog.AttachedNames);
    }

    private sealed class FakeProcessInspector : IProcessOwnedEtwSessionProcessInspector
    {
        private readonly Dictionary<ProcessOwnedEtwSessionOwner, Queue<ProcessOwnedEtwSessionOwnerState>> states = [];

        public void Set(
            ProcessOwnedEtwSessionOwner owner,
            params ProcessOwnedEtwSessionOwnerState[] sequence)
        {
            states[owner] = new Queue<ProcessOwnedEtwSessionOwnerState>(sequence);
        }

        public ProcessOwnedEtwSessionOwnerState Inspect(ProcessOwnedEtwSessionOwner owner)
        {
            if (!states.TryGetValue(owner, out var sequence) || sequence.Count == 0)
            {
                return ProcessOwnedEtwSessionOwnerState.Unknown;
            }

            return sequence.Count == 1
                ? sequence.Peek()
                : sequence.Dequeue();
        }
    }

    private sealed class FakeSessionCatalog(IReadOnlyList<string> sessionNames) : IProcessOwnedEtwSessionCatalog
    {
        public Exception? EnumerationFailure { get; init; }
        public List<string> AttachedNames { get; } = [];
        public List<string> StoppedNames { get; } = [];
        public HashSet<string> StopFailures { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> GetActiveSessionNames()
        {
            if (EnumerationFailure is not null)
            {
                throw EnumerationFailure;
            }

            return sessionNames;
        }

        public IProcessOwnedEtwSessionAttachment? TryAttach(string sessionName)
        {
            AttachedNames.Add(sessionName);
            return new FakeAttachment(this, sessionName);
        }

        private sealed class FakeAttachment(FakeSessionCatalog owner, string sessionName) : IProcessOwnedEtwSessionAttachment
        {
            public bool Stop()
            {
                if (owner.StopFailures.Contains(sessionName))
                {
                    throw new InvalidOperationException("fixture stop failure");
                }

                owner.StoppedNames.Add(sessionName);
                return true;
            }

            public void Dispose()
            {
            }
        }
    }
}
