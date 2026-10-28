using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security;

public sealed class TrustedAccessRequestOwner(
    ITrustedAccessKeyService keyService,
    IRuntimePlanProvider runtimePlanProvider,
    TimeProvider timeProvider) : ITrustedAccessRequestOwner, IHostedService
{
    private const int MaximumRequestRecords = 128;
    private const int MaximumRequestsPerMinute = 8;
    private const int MaximumActiveWaiters = 16;
    private const int MaximumActiveSubscribers = 32;
    private static readonly TimeSpan WaitMaximumDuration =
        TimeSpan.FromMilliseconds(TrustedAccessApiContract.AccessRequestWaitMaximumMilliseconds);
    private static readonly TimeSpan RequestRateWindow = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);
    private readonly Dictionary<string, RequestEntry> requests = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> requestTimes = new();
    private TaskCompletionSource<bool> changed = NewSignal();
    private CancellationTokenSource? expirationPumpCancellation;
    private Task? expirationPump;
    private int activeWaiters;
    private int activeSubscribers;
    private bool hasPendingRequests;

    internal bool HasPendingRequests => Volatile.Read(ref hasPendingRequests);
    internal event Action? PendingRequestsChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (expirationPump is not null)
            throw new InvalidOperationException("The trusted-access request owner is already started.");
        expirationPumpCancellation = new CancellationTokenSource();
        expirationPump = RunExpirationPumpAsync(expirationPumpCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cancellation = Interlocked.Exchange(ref expirationPumpCancellation, null);
        var pump = Interlocked.Exchange(ref expirationPump, null);
        if (cancellation is null)
            return;
        await cancellation.CancelAsync();
        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        cancellation.Dispose();
    }

    public async ValueTask<TrustedAccessRequestCreateResult> CreateAsync(
        TrustedAccessRequestCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValidCreateCommand(command))
            return new(null, null, null, "request-invalid");

        await SweepExpiredAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var lifetime = RequestLifetime();
        var requestId = RandomNumberGenerator.GetHexString(64);
        var deliverySecret = RandomNumberGenerator.GetHexString(64);
        var request = new RequestEntry(
            requestId,
            command.Name.Trim(),
            command.RequestedScopes.ToArray(),
            command.ObservedExecutablePath,
            command.ObservedProcessId,
            now,
            now + lifetime,
            HashSecret(deliverySecret));

        lock (gate)
        {
            TrimRateWindow(now);
            if (requests.Count >= MaximumRequestRecords)
                return new(null, null, null, "request-capacity-reached");
            if (requestTimes.Count >= MaximumRequestsPerMinute)
                return new(null, null, null, "request-rate-limited");
            if (!requests.TryAdd(requestId, request))
                return new(null, null, null, "request-id-collision");

            requestTimes.Enqueue(now);
            SignalChangedUnderLock();
        }

        return new(requestId, deliverySecret, request.ExpiresAt, null);
    }

    public async IAsyncEnumerable<IReadOnlyList<TrustedAccessRequestPendingSnapshot>> SubscribePendingAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (activeSubscribers >= MaximumActiveSubscribers)
                throw new InvalidOperationException("Access-request subscription capacity reached.");
            activeSubscribers++;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SweepExpiredAsync(cancellationToken);

                IReadOnlyList<TrustedAccessRequestPendingSnapshot> snapshot;
                Task changeTask;
                TimeSpan? untilExpiry;
                lock (gate)
                {
                    var pending = requests.Values
                        .Where(static request => request.Status == TrustedAccessRequestStatus.Pending)
                        .OrderBy(static request => request.SubmittedAt)
                        .Select(static request => request.ToPendingSnapshot())
                        .ToArray();
                    snapshot = pending;
                    changeTask = changed.Task;
                    untilExpiry = pending.Length == 0
                        ? null
                        : pending.Min(static request => request.ExpiresAt) - timeProvider.GetUtcNow();
                }

                yield return snapshot;
                if (untilExpiry is null)
                {
                    await changeTask.WaitAsync(cancellationToken);
                    continue;
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(
                    untilExpiry.Value > TimeSpan.Zero ? untilExpiry.Value : TimeSpan.Zero,
                    timeProvider,
                    delayCancellation.Token);
                await Task.WhenAny(changeTask, delay);
                await delayCancellation.CancelAsync();
            }
        }
        finally
        {
            lock (gate)
            {
                activeSubscribers--;
            }
        }
    }

    public async ValueTask<TrustedAccessRequestWaitResult> WaitAsync(
        string requestId,
        string deliverySecret,
        CancellationToken cancellationToken)
    {
        if (!IsValidProofInput(requestId, deliverySecret))
            return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-proof-invalid");

        await SweepExpiredAsync(cancellationToken);
        var proof = HashSecret(deliverySecret);
        lock (gate)
        {
            if (!requests.TryGetValue(requestId, out var request) || !HasValidProof(request, proof))
                return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-proof-invalid");
            if (request.WaiterActive)
                return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-wait-already-active");
            if (activeWaiters >= MaximumActiveWaiters)
                return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-wait-capacity-reached");
            request.WaiterActive = true;
            activeWaiters++;
        }

        var waitDeadline = timeProvider.GetUtcNow() + WaitMaximumDuration;
        try
        {
            while (true)
            {
                await SweepExpiredAsync(cancellationToken);
                Task changeTask;
                TimeSpan untilWake;
                lock (gate)
                {
                    if (!requests.TryGetValue(requestId, out var request) || !HasValidProof(request, proof))
                        return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-proof-invalid");

                    switch (request.Status)
                    {
                        case TrustedAccessRequestStatus.Approved when request.ApiKey is not null:
                            var apiKey = request.ApiKey;
                            request.ApiKey = null;
                            request.Status = TrustedAccessRequestStatus.Delivered;
                            request.TerminalUntil = timeProvider.GetUtcNow() + RequestLifetime();
                            SignalChangedUnderLock();
                            return new(TrustedAccessRequestStatus.Approved, request.Key, apiKey);
                        case TrustedAccessRequestStatus.Approved:
                        case TrustedAccessRequestStatus.Delivered:
                            return new(TrustedAccessRequestStatus.Delivered);
                        case TrustedAccessRequestStatus.Denied:
                            return new(TrustedAccessRequestStatus.Denied, DenialReason: request.DenialReason);
                        case TrustedAccessRequestStatus.Expired:
                            return new(TrustedAccessRequestStatus.Expired);
                    }

                    var now = timeProvider.GetUtcNow();
                    if (now >= waitDeadline)
                        return new(TrustedAccessRequestStatus.Pending);

                    changeTask = changed.Task;
                    var requestLifetime = request.ExpiresAt - now;
                    var remainingWait = waitDeadline - now;
                    untilWake = requestLifetime < remainingWait ? requestLifetime : remainingWait;
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(
                    untilWake > TimeSpan.Zero ? untilWake : TimeSpan.Zero,
                    timeProvider,
                    delayCancellation.Token);
                await Task.WhenAny(changeTask, delay);
                await delayCancellation.CancelAsync();
            }
        }
        finally
        {
            lock (gate)
            {
                if (requests.TryGetValue(requestId, out var request))
                    request.WaiterActive = false;
                activeWaiters--;
            }
        }
    }

    public async ValueTask<TrustedAccessRequestDecisionResult> ApproveAsync(
        string requestId,
        IReadOnlyList<string> scopes,
        IReadOnlyCollection<string> approverScopes,
        bool approverIsAdministrator,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length != 64)
            return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-not-found");
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(approverScopes);

        await SweepExpiredAsync(cancellationToken);
        RequestEntry request;
        lock (gate)
        {
            if (!requests.TryGetValue(requestId, out request!))
                return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-not-found");
            if (request.Status != TrustedAccessRequestStatus.Pending)
                return new(request.Status, request.Key, request.DenialReason, "request-not-pending");
            if (request.ApprovalInProgress)
                return new(request.Status, ErrorCode: "request-approval-in-progress");
            if (scopes.Count == 0
                || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count
                || scopes.Any(scope => !request.RequestedScopes.Contains(scope, StringComparer.Ordinal)))
            {
                return new(request.Status, ErrorCode: "approved-scope-not-requested");
            }
            if (!approverIsAdministrator
                && scopes.Any(scope => !approverScopes.Contains(scope, StringComparer.Ordinal)))
            {
                return new(request.Status, ErrorCode: "approved-scope-exceeds-grant");
            }
            request.ApprovalInProgress = true;
        }

        AccessKeyCreated created;
        try
        {
            created = await keyService.CreateAsync(
                new AccessKeyCreateRequest(request.Name, AccessKeyPurposes.Control, scopes.ToArray()),
                cancellationToken);
        }
        catch
        {
            lock (gate)
            {
                if (requests.TryGetValue(requestId, out var current))
                    current.ApprovalInProgress = false;
                SignalChangedUnderLock();
            }
            throw;
        }

        var revokeCreatedKey = false;
        TrustedAccessRequestDecisionResult result;
        lock (gate)
        {
            if (!requests.TryGetValue(requestId, out var current))
            {
                revokeCreatedKey = true;
                result = new(TrustedAccessRequestStatus.Expired, ErrorCode: "request-expired");
            }
            else if (current.Status != TrustedAccessRequestStatus.Pending
                || timeProvider.GetUtcNow() >= current.ExpiresAt)
            {
                current.ApprovalInProgress = false;
                current.Status = TrustedAccessRequestStatus.Expired;
                current.TerminalUntil = timeProvider.GetUtcNow() + RequestLifetime();
                current.RevocationKeyId = created.Credential.Id;
                revokeCreatedKey = true;
                SignalChangedUnderLock();
                result = new(TrustedAccessRequestStatus.Expired, ErrorCode: "request-expired");
            }
            else
            {
                current.ApprovalInProgress = false;
                current.Status = TrustedAccessRequestStatus.Approved;
                current.ExpiresAt = timeProvider.GetUtcNow() + RequestLifetime();
                current.TerminalUntil = null;
                current.Key = created.Credential;
                current.ApiKey = created.ApiKey;
                SignalChangedUnderLock();
                result = new(TrustedAccessRequestStatus.Approved, created.Credential);
            }
        }

        if (revokeCreatedKey)
        {
            await keyService.RevokeAsync(created.Credential.Id, CancellationToken.None);
            lock (gate)
            {
                if (requests.TryGetValue(requestId, out var current)
                    && StringComparer.Ordinal.Equals(current.RevocationKeyId, created.Credential.Id))
                    current.RevocationKeyId = null;
            }
        }
        return result;
    }

    public async ValueTask<TrustedAccessRequestDecisionResult> DenyAsync(
        string requestId,
        TrustedAccessRequestDenialReason reason,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(reason))
            return new(TrustedAccessRequestStatus.Pending, ErrorCode: "denial-reason-invalid");
        await SweepExpiredAsync(cancellationToken);
        lock (gate)
        {
            if (!requests.TryGetValue(requestId, out var request))
                return new(TrustedAccessRequestStatus.Pending, ErrorCode: "request-not-found");
            if (request.Status != TrustedAccessRequestStatus.Pending)
                return new(request.Status, request.Key, request.DenialReason, "request-not-pending");
            if (request.ApprovalInProgress)
                return new(request.Status, ErrorCode: "request-approval-in-progress");

            request.Status = TrustedAccessRequestStatus.Denied;
            request.DenialReason = reason;
            request.TerminalUntil = timeProvider.GetUtcNow() + RequestLifetime();
            SignalChangedUnderLock();
            return new(TrustedAccessRequestStatus.Denied, DenialReason: reason);
        }
    }

    private async ValueTask SweepExpiredAsync(CancellationToken cancellationToken)
    {
        await maintenanceGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var revokeRequests = new List<(string RequestId, string KeyId)>();
            lock (gate)
            {
                if (requests.Count == 0)
                {
                    TrimRateWindow(now);
                    return;
                }
                var lifetime = RequestLifetime();
                var changedState = false;
                foreach (var request in requests.Values.ToArray())
                {
                    if (request.RevocationKeyId is { } revocationKeyId)
                        revokeRequests.Add((request.RequestId, revocationKeyId));

                    if (request.Status is TrustedAccessRequestStatus.Pending or TrustedAccessRequestStatus.Approved
                        && now >= request.ExpiresAt)
                    {
                        // If approval happened but the one-time secret was never collected,
                        // expire only this delivery request. The named key remains visible
                        // for an explicit manager revoke; keys do not expire automatically.
                        request.Status = TrustedAccessRequestStatus.Expired;
                        request.ApiKey = null;
                        request.TerminalUntil = now + lifetime;
                        changedState = true;
                    }

                    if (request.Status is TrustedAccessRequestStatus.Denied
                        or TrustedAccessRequestStatus.Expired
                        or TrustedAccessRequestStatus.Delivered)
                    {
                        request.TerminalUntil ??= now + lifetime;
                        if (now >= request.TerminalUntil.Value
                            && request.RevocationKeyId is null
                            && !request.ApprovalInProgress)
                        {
                            requests.Remove(request.RequestId);
                            changedState = true;
                        }
                    }
                }

                TrimRateWindow(now);
                if (changedState)
                    SignalChangedUnderLock();
            }

            foreach (var (requestId, keyId) in revokeRequests)
            {
                // This path is only used when key creation raced request expiry.
                await keyService.RevokeAsync(keyId, CancellationToken.None);
                lock (gate)
                {
                    if (requests.TryGetValue(requestId, out var request)
                        && StringComparer.Ordinal.Equals(request.RevocationKeyId, keyId))
                        request.RevocationKeyId = null;
                }
            }
        }
        finally
        {
            maintenanceGate.Release();
        }
    }

    private async Task RunExpirationPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SweepExpiredAsync(cancellationToken);
                Task changeTask;
                TimeSpan? untilExpiry;
                lock (gate)
                {
                    changeTask = changed.Task;
                    var nextExpiry = requests.Values
                        .Select(static request => request.Status switch
                        {
                            TrustedAccessRequestStatus.Pending => request.ExpiresAt,
                            TrustedAccessRequestStatus.Approved when request.ApiKey is not null => request.ExpiresAt,
                            TrustedAccessRequestStatus.Denied or TrustedAccessRequestStatus.Expired
                                or TrustedAccessRequestStatus.Delivered => request.TerminalUntil,
                            _ => null
                        })
                        .Where(static expiresAt => expiresAt.HasValue)
                        .Select(static expiresAt => expiresAt!.Value)
                        .DefaultIfEmpty()
                        .Min();
                    untilExpiry = nextExpiry == default
                        ? null
                        : nextExpiry - timeProvider.GetUtcNow();
                }

                if (untilExpiry is null)
                {
                    await changeTask.WaitAsync(cancellationToken);
                    continue;
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(
                    untilExpiry.Value > TimeSpan.Zero ? untilExpiry.Value : TimeSpan.Zero,
                    timeProvider,
                    delayCancellation.Token);
                await Task.WhenAny(changeTask, delay);
                await delayCancellation.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan RequestLifetime()
    {
        var milliseconds = runtimePlanProvider.Current.HostManager.TrustedAccessRequestLifetimeMilliseconds;
        if (milliseconds <= 0)
            throw new InvalidOperationException("The trusted-access request lifetime is unavailable.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void TrimRateWindow(DateTimeOffset now)
    {
        while (requestTimes.TryPeek(out var oldest) && now - oldest >= RequestRateWindow)
            requestTimes.Dequeue();
    }

    private void SignalChangedUnderLock()
    {
        var pending = requests.Count != 0;
        if (hasPendingRequests != pending)
        {
            Volatile.Write(ref hasPendingRequests, pending);
            PendingRequestsChanged?.Invoke();
        }
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsValidCreateCommand(TrustedAccessRequestCreateCommand command)
        => !string.IsNullOrWhiteSpace(command.Name)
            && command.Name.Trim().Length <= 80
            && command.RequestedScopes.Count is > 0 and <= 11
            && command.RequestedScopes.Distinct(StringComparer.Ordinal).Count() == command.RequestedScopes.Count
            && command.ObservedProcessId > 0
            && !string.IsNullOrWhiteSpace(command.ObservedExecutablePath)
            && command.ObservedExecutablePath.Length <= 32_767;

    private static bool IsValidProofInput(string requestId, string deliverySecret)
        => requestId.Length == 64
            && requestId.All(char.IsAsciiHexDigit)
            && deliverySecret.Length == 64
            && deliverySecret.All(char.IsAsciiHexDigit);

    private static byte[] HashSecret(string secret)
        => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static bool HasValidProof(RequestEntry entry, byte[] proofHash)
        => entry.DeliverySecretHash.Length == proofHash.Length
            && CryptographicOperations.FixedTimeEquals(entry.DeliverySecretHash, proofHash);

    private sealed class RequestEntry(
        string requestId,
        string name,
        string[] requestedScopes,
        string observedExecutablePath,
        int observedProcessId,
        DateTimeOffset submittedAt,
        DateTimeOffset expiresAt,
        byte[] deliverySecretHash)
    {
        public string RequestId { get; } = requestId;
        public string Name { get; } = name;
        public string[] RequestedScopes { get; } = requestedScopes;
        public string ObservedExecutablePath { get; } = observedExecutablePath;
        public int ObservedProcessId { get; } = observedProcessId;
        public DateTimeOffset SubmittedAt { get; } = submittedAt;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public byte[] DeliverySecretHash { get; } = deliverySecretHash;
        public TrustedAccessRequestStatus Status { get; set; } = TrustedAccessRequestStatus.Pending;
        public TrustedAccessRequestDenialReason? DenialReason { get; set; }
        public AccessKeySummary? Key { get; set; }
        public string? ApiKey { get; set; }
        public string? RevocationKeyId { get; set; }
        public DateTimeOffset? TerminalUntil { get; set; }
        public bool ApprovalInProgress { get; set; }
        public bool WaiterActive { get; set; }

        public TrustedAccessRequestPendingSnapshot ToPendingSnapshot()
            => new(RequestId, Name, RequestedScopes.ToArray(), ObservedExecutablePath,
                ObservedProcessId, SubmittedAt, ExpiresAt);
    }
}
