using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Infrastructure.Operations;
using static ResourceManager.App.Infrastructure.Optimization.ProcessEffectValidationScopeDocuments;

namespace ResourceManager.App.Infrastructure.Optimization;

public interface IHostManagerUserDataArrival
{
    void Prepare(string incomingRoot, string targetRoot, string? sourceRoot = null);
}

/// <summary>
/// Receives data in an unpublished installation tree. Recovery stores travel
/// unchanged; validation grants belong to the old runtime and become evidence.
/// The caller promotes UserData and AuthorityDirectory together or neither.
/// </summary>
public sealed class HostManagerUserDataArrival : IHostManagerUserDataArrival
{
    public const string AuthorityDirectory = ".resource-manager-validation-authority";
    private const string Generation = "generation-00000001";
    private const string StateFile = "process-effect-scope.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static IHostManagerUserDataArrival Instance { get; } = new HostManagerUserDataArrival(
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        WindowsHostManagerProcessEffectValidationScopeFileCommitter.Instance);
    private readonly SecurityIdentifier owner;
    private readonly IHostManagerProcessEffectValidationScopeFileCommitter committer;

    internal HostManagerUserDataArrival(SecurityIdentifier owner,
        IHostManagerProcessEffectValidationScopeFileCommitter committer)
    {
        this.owner = owner;
        this.committer = committer;
    }

    internal static (string StatePath, string AuthorityManifestPath) Paths(string root)
        => (Path.Combine(root, "UserData", "HostManager", "Validation", Generation, StateFile),
            Path.Combine(root, AuthorityDirectory, "process-effect-scope-generation-00000001.json"));

    public void Prepare(string incomingRoot, string targetRoot, string? sourceRoot = null)
    {
        incomingRoot = Path.GetFullPath(incomingRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        var incoming = Paths(incomingRoot);
        var target = Paths(targetRoot);
        ReleasePackageLayout.RejectReparse(incomingRoot);
        var hostManager = Path.Combine(incomingRoot, "UserData", "HostManager");
        RequireDirectoryIfPresent(hostManager);
        RequireDirectoryIfPresent(Path.Combine(targetRoot, "UserData", "HostManager"));
        RequireDirectoryIfPresent(Path.GetDirectoryName(target.AuthorityManifestPath)!);
        var entries = Directory.Exists(hostManager) ? OwnedPackageTree.Read(hostManager) : [];
        if (sourceRoot is not null)
        {
            var source = Paths(Path.GetFullPath(sourceRoot));
            RequireDirectoryIfPresent(Path.GetDirectoryName(source.AuthorityManifestPath)!);
            if (Directory.Exists(Path.GetDirectoryName(source.AuthorityManifestPath)))
                ValidateAuthority(source.StatePath, source.AuthorityManifestPath);
            else
                WindowsProcessEffectValidationScopeStorage.InitializeUnclaimedEmptyDirectoryIfPresent(source.StatePath, initialize: false);
        }
        // Target obligations are checked even when no validation namespace has
        // ever existed: configured journal/ledger roots also live below UserData.
        if (sourceRoot is null && !incomingRoot.Equals(targetRoot, StringComparison.OrdinalIgnoreCase))
            RequireSettledTarget(targetRoot);
        if (entries.Count == 0 && !Directory.Exists(Path.Combine(targetRoot, "UserData", "HostManager"))
            && !Directory.Exists(Path.Combine(targetRoot, AuthorityDirectory))) return;
        // Finish all input checks before changing even the unpublished tree.
        ValidateNamespace(incoming.StatePath);
        foreach (var store in HostManagerDurableStorePaths.Resolve(incomingRoot))
            ValidateStoreIfPresent(store);
        if (Directory.Exists(Path.GetDirectoryName(incoming.AuthorityManifestPath)))
            throw new InvalidDataException("Incoming user data must not carry an installation authority.");

        using var privilege = WindowsValidationStorageOwnerPrivilege.Acquire(owner);
        var generationDirectory = Path.GetDirectoryName(incoming.StatePath)!;
        string? retainedScope = null;
        if (Directory.Exists(generationDirectory))
        {
            if (Directory.EnumerateFileSystemEntries(generationDirectory).Any())
            {
                var previous = Path.Combine(Path.GetDirectoryName(generationDirectory)!, "PreviousInstallations");
                Directory.CreateDirectory(previous);
                retainedScope = Path.Combine(previous, Guid.NewGuid().ToString("N"));
                Directory.Move(generationDirectory, retainedScope);
            }
            else Directory.Delete(generationDirectory);
        }
        // Rebuild only the protected validation namespace. Journals, payloads,
        // ownership and rollback stores keep their bytes and recovery identities.
        using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(incoming.StatePath, true, owner)) { }
        var manifest = new HostManagerProcessEffectValidationAuthorityManifest(
            AuthorityManifestSchemaVersion, AuthorityManifestContract, Guid.NewGuid(),
            ComputeStatePathIdentitySha256(target.StatePath),
            HostManagerProcessEffectValidationAuthorityManifestState.Ready, string.Empty);
        manifest = manifest with { ChecksumSha256 = ComputeAuthorityManifestChecksum(manifest) };
        WriteProtected(incoming.AuthorityManifestPath, manifest);
        // Visible transfer evidence names the retained scope and the existing
        // recovery owner; it is never used as an active scope or a recovery ack.
        var receiptPath = Path.Combine(Path.GetDirectoryName(incoming.AuthorityManifestPath)!, "user-data-arrival.json");
        WriteProtected(receiptPath, new
        {
            Contract = "host-manager-user-data-arrival-v1",
            Recovery = "carried-to-existing-runtime-owners",
            PreviousScope = retainedScope is null ? null : Path.GetRelativePath(incomingRoot, retainedScope),
            TargetStatePathSha256 = manifest.StatePathSha256
        });
    }

    private void WriteProtected<T>(string path, T document)
    {
        using var directory = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(path, true, owner);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(true);
            }
            WindowsProcessEffectValidationScopeStorage.ApplySecureFileAcl(directory, temporary, owner);
            committer.Commit(temporary, path, false);
            using var verified = WindowsProcessEffectValidationScopeStorage.OpenReadExact(directory, path, owner);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ValidateAuthority(string statePath, string manifestPath)
    {
        using var directory = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(manifestPath, false, owner);
        using var stream = WindowsProcessEffectValidationScopeStorage.OpenReadExact(directory, manifestPath, owner);
        var manifest = Read<HostManagerProcessEffectValidationAuthorityManifest>(stream, MaximumAuthorityManifestBytes);
        if (manifest.SchemaVersion != AuthorityManifestSchemaVersion || manifest.Contract != AuthorityManifestContract
            || manifest.StoreId == Guid.Empty
            || manifest.State is not (HostManagerProcessEffectValidationAuthorityManifestState.Initializing
                or HostManagerProcessEffectValidationAuthorityManifestState.Ready)
            || manifest.StatePathSha256 != ComputeStatePathIdentitySha256(statePath)
            || manifest.ChecksumSha256 != ComputeAuthorityManifestChecksum(manifest))
            throw new InvalidDataException("Incoming validation authority is invalid.");
        if (manifest.State == HostManagerProcessEffectValidationAuthorityManifestState.Initializing)
        {
            // Same interrupted first-use rule as the runtime initializer: no
            // lifecycle document may have been published before Ready.
            if (File.Exists(statePath) || File.Exists(statePath + AuditFencePathSuffix))
                throw new InvalidDataException("Initializing source authority contains lifecycle documents.");
            if (!Directory.Exists(Path.GetDirectoryName(statePath))) return;
        }
        using var stateDirectory = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(statePath, false, owner);
        foreach (var path in Directory.EnumerateFiles(stateDirectory.DirectoryPath))
        {
            using var file = WindowsProcessEffectValidationScopeStorage.OpenReadExact(stateDirectory, path, owner);
        }
    }

    private static HostManagerProcessEffectValidationAuditFenceDocument? ValidateNamespace(string statePath)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        if (!Directory.Exists(directory)) return null;
        var entries = OwnedPackageTree.Read(directory);
        var fencePath = statePath + AuditFencePathSuffix;
        if (entries.Any(path => path != directory && path != statePath && path != fencePath))
            throw new InvalidDataException("Incoming validation namespace contains an unknown document.");
        var state = ReadIfPresent<HostManagerProcessEffectValidationScopeDocument>(statePath, MaximumDocumentBytes);
        var fence = ReadIfPresent<HostManagerProcessEffectValidationAuditFenceDocument>(fencePath, MaximumAuditFenceBytes);
        if (state is null && fence is null) return null;
        if (fence is null) throw new InvalidDataException("Incoming scope lost its admission fence.");
        ValidateAuditFenceDocument(fence);
        if (state is null) { ValidateOrphanLifecycleAuditFence(fence); return fence; }
        ValidateDocument(state);
        ValidateAuditFenceScopeBinding(state, fence);
        switch (fence.State)
        {
            case HostManagerProcessEffectValidationAuditFenceState.Clean: ValidateCleanAuditFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.Intent:
            case HostManagerProcessEffectValidationAuditFenceState.AuditCommitted: ValidatePreHandoffAuditFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.HandoffDeclared:
            case HostManagerProcessEffectValidationAuditFenceState.JournalPrepared: ValidateLoadedEffectAdmissionFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.Opening: ValidateOpeningAuditFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.Closing: ValidateClosingAuditFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.Closed: ValidateClosedAuditFenceBinding(state, fence); break;
            case HostManagerProcessEffectValidationAuditFenceState.RetiringClosed: ValidateRetiringClosedAuditFenceBinding(state, fence); break;
            default: throw new InvalidDataException("Unknown incoming validation lifecycle state.");
        }
        return fence;
    }

    private static void RequireSettledTarget(string root)
    {
        var fence = ValidateNamespace(Paths(root).StatePath);
        if (fence is not null && (fence.CommittedHandoffs.Count != 0 || fence.DurableHandoff is not null))
            throw new InvalidOperationException("Settle this installation's recovery obligations before importing user data.");
        var userData = Path.Combine(root, "UserData");
        if (Directory.Exists(userData)) _ = OwnedPackageTree.Read(userData);
        Span<byte> header = stackalloc byte[128];
        foreach (var store in HostManagerDurableStorePaths.Resolve(root))
        {
            ValidateStoreIfPresent(store);
            var canonical = store.CanonicalPath;
            var kind = store.Kind;
            if (!RegularFileExists(canonical)) continue;
            // A nonempty native journal belongs to this installation; importing
            // another journal may not replace it. Its owner must settle it first.
            if (kind is "transaction-journal" or "applied-ownership")
            {
                using var stream = File.OpenRead(canonical);
                stream.ReadExactly(header[..128]);
                if (stream.Length != 128 || BinaryPrimitives.ReadUInt32LittleEndian(header[48..]) != 0)
                    throw new InvalidOperationException($"Settle the current {kind} before importing user data.");
            }
            else if (kind == "operation-coordinator")
            {
                if (HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(canonical))
                    throw new InvalidOperationException("Settle the current operation-coordinator outstanding or uncertain obligations before importing user data.");
            }
            else
            {
                using var stream = File.OpenRead(canonical);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Current recovery document exceeds its bound.");
                using var document = JsonDocument.Parse(stream);
                var value = document.RootElement;
                var records = kind == "rollback-state"
                    ? value.GetProperty("state").GetProperty("appliedPlacements")
                    : value.GetProperty("entries");
                if (records.GetArrayLength() != 0)
                    throw new InvalidOperationException($"Settle the current {kind} before importing user data.");
            }
        }
    }

    private static void ValidateStoreIfPresent(HostManagerDurableStorePath store)
    {
        var canonicalExists = RegularFileExists(store.CanonicalPath);
        var manifestExists = RegularFileExists(store.ManifestPath);
        if (canonicalExists && !manifestExists)
            throw new InvalidDataException($"The {store.Kind} recovery store has no durable root manifest: {store.CanonicalPath}");
        if (manifestExists && HostManagerDurableRootManifest.ValidateForTransfer(store.CanonicalPath, store.ManifestPath) != store.Kind)
            throw new InvalidDataException($"The {store.Kind} durable root manifest identifies another store: {store.ManifestPath}");
    }

    private static bool RegularFileExists(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"The recovery store must be a regular file: {path}");
        return true;
    }

    private static void RequireDirectoryIfPresent(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Host Manager storage must be a regular directory.");
    }

    private static T? ReadIfPresent<T>(string path, int limit) where T : class
    {
        try { using var stream = File.OpenRead(path); return Read<T>(stream, limit); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static T Read<T>(Stream stream, int limit)
    {
        if (stream.Length is <= 0 || stream.Length > limit) throw new InvalidDataException("Invalid incoming validation document length.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException("Empty incoming validation document.");
    }
}
