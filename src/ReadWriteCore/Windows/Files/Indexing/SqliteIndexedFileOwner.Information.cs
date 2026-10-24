using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.ReadWrite.Information;
namespace ResourceManager.ReadWrite.Windows.Files.Indexing;

// Original DB/query/refresh owner. Query-result publications belong to an explicit
// fixed information query, never a process-wide last-search slot.
public partial class SqliteIndexedFileOwner : IDisposable
{
    private int disposed;
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    internal sealed record EntryBinding(long EntryId, string SoftwareId, string RootPath, string RelativePath);
    internal static readonly DeviceInformationDefinition[] Definitions = [DeviceInformationFieldRegistry.FileAllIndexedModifiedTimeActual,
        DeviceInformationFieldRegistry.FileAllIndexedPathActual, DeviceInformationFieldRegistry.FileAllIndexedSizeActual];
    public Task<IReadOnlyList<SoftwareFileSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken,
        Func<string, string?, bool>? canReadPath = null)
    { ObjectDisposedException.ThrowIf(IsDisposed, this); return SearchCoreAsync(query, limit, cancellationToken, canReadPath); }
    public IndexedFileInformationQuery CreateInformationQuery(string query, int limit, Func<string, string?, bool>? canReadPath = null)
    { ObjectDisposedException.ThrowIf(IsDisposed, this); return new(this, query, limit, canReadPath); }
    internal Task<IReadOnlyList<SoftwareFileSearchResult>> SearchInformationRowsAsync(string query, int limit,
        CancellationToken token, Func<string, string?, bool>? canReadPath)
    { ObjectDisposedException.ThrowIf(IsDisposed, this); return SearchCoreAsync(query, limit, token, canReadPath); }
    internal async Task<IReadOnlyList<SoftwareFileSearchResult?>> ReadInformationRowsAsync(ImmutableArray<EntryBinding> bindings,
        Func<string, string?, bool>? canReadPath, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var rows = new List<SoftwareFileSearchResult?>(bindings.Length);
        await using var connection = await database.OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested(); SoftwareFileSearchResult? row = null;
            if (canReadPath is null || canReadPath(binding.RootPath, binding.RelativePath))
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = CandidateProjection + " FROM software_index_entries entry JOIN software_index_roots root ON root.id=entry.root_id WHERE entry.id=$id AND root.software_id=$software AND root.root_path=$root AND entry.relative_path=$path;";
                command.Parameters.AddWithValue("$id", binding.EntryId); command.Parameters.AddWithValue("$software", binding.SoftwareId);
                command.Parameters.AddWithValue("$root", binding.RootPath); command.Parameters.AddWithValue("$path", binding.RelativePath);
                await using var reader = await command.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token)) row = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    Path.Combine(reader.GetString(3), reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero));
            }
            rows.Add(row);
        }
        token.ThrowIfCancellationRequested(); return rows;
    }
    internal string EntryIdentity(EntryBinding binding) => JsonSerializer.Serialize(new[] { database.Identity,
        binding.EntryId.ToString(CultureInfo.InvariantCulture), binding.SoftwareId, binding.RootPath, binding.RelativePath });
    internal EntryBinding ParseEntryIdentity(string identity)
    {
        var parts = JsonSerializer.Deserialize<string[]>(identity);
        if (parts is not { Length: 5 } || parts[0] != database.Identity ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) throw new ArgumentException("Invalid indexed entry address.");
        var binding = new EntryBinding(id, parts[2], parts[3], parts[4]);
        if (EntryIdentity(binding) != identity) throw new ArgumentException("Noncanonical indexed entry address.");
        return binding;
    }
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
