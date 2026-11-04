using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
namespace ResourceManager.ReadWrite.Windows.Files.Indexing;

/// <summary>Explicit fixed query/limit/access scope. One current and publication
/// belong to this result owner; DB, index refresh and native lease pool are borrowed.</summary>
public sealed class IndexedFileInformationQuery : IDisposable
{
    public const string InformationItemId = "information.indexed-files";
    private readonly SqliteIndexedFileOwner owner;
    private readonly string query;
    private readonly int limit;
    private readonly Func<string, string?, bool>? canReadPath;
    private const string IdentityPrefix = "sqlite-indexed-file|";
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object gate = new();
    private DeviceInformationSnapshot current = DeviceInformationSnapshot.Empty;
    private int disposed;
    public SamplingPublicationSignal Publications { get; } = new();
    public InformationReadWriteBackend Backend { get; }
    internal IndexedFileInformationQuery(SqliteIndexedFileOwner owner, string query, int limit, Func<string, string?, bool>? canReadPath)
    {
        ArgumentNullException.ThrowIfNull(query); if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        this.owner = owner; this.query = query; this.limit = limit; this.canReadPath = canReadPath;
        Backend = new(ReadInformation, token => CaptureAsync(token), _ => new(true, null, []),
            (selection, token) => CaptureSelectedAsync(selection, token));
    }
    /// <summary>One immutable current. Field overrides retain actual timestamps
    /// when a later partial publication changes the enclosing snapshot time.</summary>
    public DeviceInformationSnapshot ReadInformation() { lock (gate) return current; }
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0 || owner.IsDisposed, this);
        return new InformationCollectionBorrow(Backend, Publications, InformationItemId, async (selection, permit, token) =>
        {
            var committed = false;
            var snapshot = await CaptureSelectedAsync(selection, token, permit.TryPublish, value => committed = value);
            return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, true));
        }, FieldCollectionSourceKind.SourceQuery);
    }
    public async ValueTask<DeviceInformationSnapshot> CaptureAsync(CancellationToken token = default, Func<Action, bool>? tryPublish = null)
    {
        await captureGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            try
            {
                var rows = await owner.SearchInformationRowsAsync(query, limit, token, canReadPath);
                token.ThrowIfCancellationRequested();
                var snapshot = new DeviceInformationSnapshot(DateTimeOffset.UtcNow,
                    rows.Select(row => Project(Identity(new(row.EntryId, row.SoftwareId, row.RootPath, row.RelativePath)), row, SqliteIndexedFileOwner.Definitions)).ToArray());
                // Discover replaces this query's membership only. No other query state exists here.
                Commit(snapshot, replaceMembership: true, tryPublish); return ReadInformation();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                var failed = SoftwareInformationFieldProjection.Clear(ReadInformation());
                Commit(failed, replaceMembership: false, tryPublish); throw;
            }
        }
        finally { captureGate.Release(); }
    }
    private async ValueTask<DeviceInformationSnapshot> CaptureSelectedAsync(ImmutableArray<InformationFieldSelection> selection,
        CancellationToken token, Func<Action, bool>? tryPublish = null, Action<bool>? completed = null)
    {
        var requested = selection.Select(field => field.Field.DeviceIdentity).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > limit || selection.Any(field => !SqliteIndexedFileOwner.Definitions.Contains(field.Definition)))
            throw new ArgumentException("Select only bound indexed-file fields.", nameof(selection));
        var bindings = requested.Select(ParseBinding).ToImmutableArray();
        await captureGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            try
            {
                var rows = await owner.ReadInformationRowsAsync(bindings, canReadPath, token);
                token.ThrowIfCancellationRequested();
                var snapshot = new DeviceInformationSnapshot(DateTimeOffset.UtcNow, requested.Select((identity, index) =>
                    Project(identity, rows[index], selection.Where(field => field.Field.DeviceIdentity == identity).Select(field => field.Definition))).ToArray());
                var committed = Commit(snapshot, false, tryPublish); completed?.Invoke(committed); return ReadInformation();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                var failed = new DeviceInformationSnapshot(null, requested.Select(identity => Project(identity, null,
                    selection.Where(field => field.Field.DeviceIdentity == identity).Select(field => field.Definition))).ToArray());
                var committed = Commit(failed, false, tryPublish); completed?.Invoke(committed); throw;
            }
        }
        finally { captureGate.Release(); }
    }
    private string Identity(SqliteIndexedFileOwner.EntryBinding binding) => IdentityPrefix + owner.EntryIdentity(binding);
    private SqliteIndexedFileOwner.EntryBinding ParseBinding(string identity)
    {
        if (!identity.StartsWith(IdentityPrefix, StringComparison.Ordinal)) throw new ArgumentException("Foreign information query field.");
        return owner.ParseEntryIdentity(identity[IdentityPrefix.Length..]);
    }
    private static DeviceInformationObject Project(string identity, SoftwareFileSearchResult? row, IEnumerable<DeviceInformationDefinition> definitions)
        => new(identity, "indexed_file", definitions.Select(definition => new DeviceInformationField(definition, row is null ? null :
            definition == SqliteIndexedFileOwner.Definitions[0] ? JsonSerializer.SerializeToElement(row.LastWriteAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) :
            definition == SqliteIndexedFileOwner.Definitions[1] ? JsonSerializer.SerializeToElement(row.FullPath) :
            row.SizeBytes < 0 ? null : JsonSerializer.SerializeToElement(((ulong)row.SizeBytes).ToString(CultureInfo.InvariantCulture)))).ToArray());
    private bool Commit(DeviceInformationSnapshot next, bool replaceMembership, Func<Action, bool>? tryPublish)
    {
        var committed = false;
        void Publish()
        {
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0 || owner.IsDisposed) return;
                var fields = replaceMembership ? new Dictionary<FieldReference, DeviceInformationField>()
                    : current.Devices.SelectMany(device => device.Fields.Select(field => KeyValuePair.Create(new FieldReference(device.DeviceIdentity, field.Definition.Name),
                        field.ObservedAt is null ? field with { ObservedAt = current.ObservedAt } : field))).ToDictionary();
                foreach (var device in next.Devices)
                foreach (var field in device.Fields)
                {
                    var reference = new FieldReference(device.DeviceIdentity, field.Definition.Name);
                    fields[reference] = field with { ObservedAt = field.Value is null ? null : next.ObservedAt };
                }
                var devices = fields.GroupBy(pair => pair.Key.DeviceIdentity).Select(group => new DeviceInformationObject(group.Key, "indexed_file", group.Select(pair => pair.Value).ToArray())).ToArray();
                current = new(next.ObservedAt, devices); committed = true;
            }
        }
        if (tryPublish is null) Publish(); else _ = tryPublish(Publish);
        if (committed) Publications.Publish([InformationItemId]);
        return committed;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0 || owner.IsDisposed, this);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        captureGate.Wait(); try { } finally { captureGate.Release(); }
        // Borrowed index/database/pool remain with their original owner. Managed gate
        // stays valid for late admitted callers; no native handle is owned by this query.
    }
}
