using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Management;

public sealed partial class ManagementInventoryRevisionOwner : IManagementInventoryRevisionOwner
{
    private const string DefaultSoftwareScope = "<default>";
    private readonly object gate = new();
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly Dictionary<string, string> softwareFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> componentFingerprintsByScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> completeComponentScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> activeComponentChanges = new(StringComparer.OrdinalIgnoreCase);
    private object componentObservationFence = new();
    private string? browserRuntimeFingerprint;
    private ulong softwareRevision;
    private ulong componentRevision;
    private ulong browserRuntimeRevision;

    public ManagementInventoryRevisionSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return CurrentLocked();
            }
        }
    }

    public ManagementInventoryComponentObservationToken CaptureComponentObservationToken()
    {
        lock (gate)
        {
            return new ManagementInventoryComponentObservationToken(componentRevision, componentObservationFence);
        }
    }

    public void ObserveSoftwareContent(
        string? windowsSid,
        IReadOnlyList<SoftwareRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var scope = string.IsNullOrWhiteSpace(windowsSid)
            ? DefaultSoftwareScope
            : windowsSid.Trim();
        var fingerprint = Fingerprint(records
            .OrderBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        var publish = false;
        lock (gate)
        {
            if (!softwareFingerprints.TryGetValue(scope, out var previous))
            {
                softwareFingerprints.Add(scope, fingerprint);
            }
            else if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                softwareFingerprints[scope] = fingerprint;
                softwareRevision = checked(softwareRevision + 1);
                publish = true;
            }
        }

        if (publish)
        {
            publicationSignal.Publish();
        }
    }

    public void ObserveSoftwareInventoryContentChanged()
    {
        lock (gate)
        {
            softwareRevision = checked(softwareRevision + 1);
        }

        publicationSignal.Publish();
    }

    public void ObserveComponentContentChanged()
    {
        lock (gate)
        {
            componentRevision = checked(componentRevision + 1);
        }

        publicationSignal.Publish();
    }

    public void ObserveComponentContentChanged(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        lock (gate)
        {
            activeComponentChanges.Remove(componentId);
            foreach (var entry in componentFingerprintsByScope)
            {
                entry.Value.Remove(componentId);
                completeComponentScopes.Remove(entry.Key);
            }
            componentRevision = checked(componentRevision + 1);
        }

        publicationSignal.Publish();
    }

    // Publish the explicit content change and its resulting status as one revision.
    // This also invalidates bindings when repaired bytes leave the status unchanged.
    public void ObserveComponentContentChanged(string? windowsSid, ComponentStatus component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var scope = NormalizeScope(windowsSid);
        var fingerprint = Fingerprint(component);
        lock (gate)
        {
            var changeWasActive = activeComponentChanges.Remove(component.Definition.Id);
            // Shared component bytes changed for every user. Other scopes must
            // re-observe their own runtime facts without publishing this repair twice.
            foreach (var entry in componentFingerprintsByScope)
            {
                if (string.Equals(entry.Key, scope, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A lease may have seeded a scope while omitting this active ID, so
                // invalidate every other observed scope even when there was no ID to remove.
                if (entry.Value.Remove(component.Definition.Id) || changeWasActive)
                {
                    completeComponentScopes.Remove(entry.Key);
                }
            }
            if (!componentFingerprintsByScope.TryGetValue(scope, out var fingerprints))
            {
                fingerprints = new(StringComparer.OrdinalIgnoreCase);
                componentFingerprintsByScope.Add(scope, fingerprints);
            }
            fingerprints[component.Definition.Id] = fingerprint;
            componentRevision = checked(componentRevision + 1);
        }
        publicationSignal.Publish();
    }

    public IDisposable BeginComponentChange(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        // The token prevents an older disposed lease from clearing a newer reservation.
        var token = new object();
        lock (gate)
        {
            if (!activeComponentChanges.TryAdd(componentId, token))
            {
                throw new InvalidOperationException($"A component change is already active for '{componentId}'.");
            }

            componentObservationFence = token;
        }

        return new ComponentChangeLease(this, componentId, token);
    }

    public void ObserveComponents(
        string? windowsSid,
        IReadOnlyList<ComponentStatus> components,
        ManagementInventoryComponentObservationToken? observedComponent = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        var scope = NormalizeScope(windowsSid);
        var next = components.ToDictionary(
            static component => component.Definition.Id,
            Fingerprint,
            StringComparer.OrdinalIgnoreCase);
        var publish = false;
        lock (gate)
        {
            if (!IsCurrentComponentObservationLocked(observedComponent))
            {
                return;
            }

            var hasPrevious = componentFingerprintsByScope.TryGetValue(scope, out var previous);
            var hasUnseededActiveComponent = PreserveActiveComponentFingerprintsLocked(
                next, hasPrevious ? previous : null);
            if (!hasPrevious)
            {
                componentFingerprintsByScope.Add(scope, next);
                completeComponentScopes.Add(scope);
            }
            else if (!completeComponentScopes.Contains(scope))
            {
                publish = KnownComponentFingerprintsChanged(previous!, next);
                componentFingerprintsByScope[scope] = next;
                completeComponentScopes.Add(scope);
                if (publish)
                {
                    componentRevision = checked(componentRevision + 1);
                }
            }
            else if (!ComponentFingerprintsEqual(previous!, next))
            {
                componentFingerprintsByScope[scope] = next;
                componentRevision = checked(componentRevision + 1);
                publish = true;
            }

            if (hasUnseededActiveComponent)
            {
                completeComponentScopes.Remove(scope);
            }
        }

        if (publish)
        {
            publicationSignal.Publish();
        }
    }

    public void ObserveComponent(
        string? windowsSid,
        ComponentStatus component,
        ManagementInventoryComponentObservationToken? observedComponent = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var scope = NormalizeScope(windowsSid);
        var id = component.Definition.Id;
        var fingerprint = Fingerprint(component);
        var publish = false;
        lock (gate)
        {
            if (!IsCurrentComponentObservationLocked(observedComponent)
                || activeComponentChanges.ContainsKey(id))
            {
                return;
            }

            if (!componentFingerprintsByScope.TryGetValue(scope, out var fingerprints))
            {
                fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                componentFingerprintsByScope.Add(scope, fingerprints);
            }
            if (!fingerprints.TryGetValue(id, out var previous))
            {
                fingerprints.Add(id, fingerprint);
            }
            else if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                fingerprints[id] = fingerprint;
                componentRevision = checked(componentRevision + 1);
                publish = true;
            }
        }

        if (publish)
        {
            publicationSignal.Publish();
        }
    }

    private bool IsCurrentComponentObservationLocked(ManagementInventoryComponentObservationToken? observedComponent)
        => observedComponent is null
            || observedComponent.ComponentRevision == componentRevision
                && ReferenceEquals(observedComponent.ChangeFence, componentObservationFence);

    private bool PreserveActiveComponentFingerprintsLocked(
        Dictionary<string, string> next,
        Dictionary<string, string>? previous)
    {
        var hasUnseededActiveComponent = false;
        foreach (var id in activeComponentChanges.Keys)
        {
            if (previous is not null && previous.TryGetValue(id, out var fingerprint))
            {
                next[id] = fingerprint;
            }
            else
            {
                next.Remove(id);
                hasUnseededActiveComponent = true;
            }
        }

        return hasUnseededActiveComponent;
    }

    private void EndComponentChange(string componentId, object token)
    {
        lock (gate)
        {
            if (activeComponentChanges.TryGetValue(componentId, out var activeToken)
                && ReferenceEquals(activeToken, token))
            {
                activeComponentChanges.Remove(componentId);
                componentObservationFence = new object();
            }
        }
    }

    private sealed class ComponentChangeLease(
        ManagementInventoryRevisionOwner owner,
        string componentId,
        object token) : IDisposable
    {
        private ManagementInventoryRevisionOwner? current = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref current, null)?.EndComponentChange(componentId, token);
        }
    }

    public void ObserveBrowserRuntimeSnapshot(BrowserRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fingerprint = Fingerprint(new BrowserRuntimeDigestInput(snapshot.InstallComponentId,
            snapshot.SharedRuntime, snapshot.BrowserFallback,
            snapshot.Candidates.OrderBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase).ToArray()));
        var publish = false;
        lock (gate)
        {
            if (browserRuntimeFingerprint is null)
            {
                browserRuntimeFingerprint = fingerprint;
            }
            else if (!string.Equals(browserRuntimeFingerprint, fingerprint, StringComparison.Ordinal))
            {
                browserRuntimeFingerprint = fingerprint;
                browserRuntimeRevision = checked(browserRuntimeRevision + 1);
                publish = true;
            }
        }

        if (publish)
        {
            publicationSignal.Publish();
        }
    }

    public async IAsyncEnumerable<ManagementInventoryRevisionSnapshot> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        using var subscription = publicationSignal.Subscribe();
        yield return Current;
        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.WaitAsync(cancellationToken);
            yield return Current;
        }
    }

    private ManagementInventoryRevisionSnapshot CurrentLocked()
        => new(
            softwareRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            componentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            browserRuntimeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string NormalizeScope(string? windowsSid)
        => string.IsNullOrWhiteSpace(windowsSid) ? DefaultSoftwareScope : windowsSid.Trim();

    private static bool KnownComponentFingerprintsChanged(
        IReadOnlyDictionary<string, string> known,
        IReadOnlyDictionary<string, string> next)
        => known.Any(pair => !next.TryGetValue(pair.Key, out var value)
            || !string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool ComponentFingerprintsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static string Fingerprint<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ManagementInventoryJsonContext.Default.Options)));
}
