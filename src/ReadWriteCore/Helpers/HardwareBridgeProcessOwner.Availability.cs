using System.ComponentModel;

namespace ResourceManager.ReadWrite.Helpers;

public enum HardwareBridgeUnavailableKind { None, InstallationInProgress, NotInstalled, RuntimeMissing, AccessDenied, HardwareUnsupported, ExitNotConfirmed }

public sealed partial class HardwareBridgeProcessOwner
{
    private volatile bool available = true;
    private HardwareBridgeUnavailableKind unavailableKind;
    private CancellationTokenSource? availabilityStop;
    private Task availabilityCheck = Task.CompletedTask;
    private HardwareBridgeChildProcess.ExitNotConfirmedException? pendingExit;
    private readonly Queue<bool> availabilityChanges = new();
    private bool publishingAvailability;
    public bool IsAvailable => available;
    public HardwareBridgeUnavailableKind UnavailableKind { get { lock (sync) return unavailableKind; } }
    public event Action<bool>? AvailabilityChanged;

    /// <summary>Publishes the current value through the same ordered stream as changes.</summary>
    public void PublishCurrentAvailability()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            availabilityChanges.Enqueue(available);
        }
        PublishAvailabilityChanges();
    }

    private void CancelAvailabilityCheckLocked()
    {
        availabilityStop?.Cancel();
        availabilityStop?.Dispose();
        availabilityStop = null;
    }

    private void InitializeAvailability()
    {
        if (resolveExecutablePath() is null) SetUnavailable(HardwareBridgeUnavailableKind.NotInstalled, "hardware-bridge-not-installed");
    }

    private void ThrowIfUnavailable()
    {
        if (available) return;
        if (unavailableKind == HardwareBridgeUnavailableKind.NotInstalled)
            throw new FileNotFoundException("HardwareBridge is not installed.");
        throw new InvalidOperationException($"HardwareBridge is unavailable: {unavailableKind}: {UnavailableReason}");
    }

    private void SetUnavailable(HardwareBridgeUnavailableKind kind, string? reason)
    {
        lock (sync) SetUnavailableLocked(kind, reason);
        PublishAvailabilityChanges();
    }

    private void SetUnavailableLocked(HardwareBridgeUnavailableKind kind, string? reason)
    {
        unavailableKind = kind;
        UnavailableReason = reason;
        CancelAvailabilityCheckLocked();
        if (available) { available = false; availabilityChanges.Enqueue(false); }
        var canChange = kind is HardwareBridgeUnavailableKind.NotInstalled or HardwareBridgeUnavailableKind.ExitNotConfirmed
            || kind == HardwareBridgeUnavailableKind.RuntimeMissing && options.IsRuntimeAvailable is not null;
        if (!disposed && !suspended && canChange && options.AvailabilityCheckInterval > TimeSpan.Zero)
        {
            availabilityStop = new();
            availabilityCheck = CheckAvailabilityAsync(kind, availabilityStop.Token);
        }
    }

    // Checks inspect files/runtime presence or the retained process handle, never
    // launch a helper. CPU support and process rights cannot change in this host.
    private async Task CheckAvailabilityAsync(HardwareBridgeUnavailableKind kind, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.AvailabilityCheckInterval, token).ConfigureAwait(false);
                HardwareBridgeChildProcess.ExitNotConfirmedException? retained;
                lock (sync)
                {
                    if (token.IsCancellationRequested || disposed || suspended || unavailableKind != kind) return;
                    retained = pendingExit;
                }
                bool ready;
                try
                {
                    ready = kind == HardwareBridgeUnavailableKind.ExitNotConfirmed ? retained?.HasExited == true
                        : resolveExecutablePath() is not null && (kind != HardwareBridgeUnavailableKind.RuntimeMissing || options.IsRuntimeAvailable!());
                }
                catch (Exception error) { warning?.Invoke(error, "hardware-bridge-availability-check"); continue; }
                lock (sync)
                {
                    if (token.IsCancellationRequested || disposed || suspended || unavailableKind != kind) return;
                    if (!ready) continue;
                    if (kind == HardwareBridgeUnavailableKind.ExitNotConfirmed)
                    {
                        pendingExit!.Dispose();
                        pendingExit = null;
                        runningStop?.Dispose();
                        runningStop = null;
                        ended = null;
                        InstallationChangedLocked();
                    }
                    else SetAvailableLocked();
                }
                PublishAvailabilityChanges();
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void SetAvailableLocked()
    {
        CancelAvailabilityCheckLocked();
        unavailableKind = HardwareBridgeUnavailableKind.None;
        UnavailableReason = null;
        if (!available) { available = true; availabilityChanges.Enqueue(true); }
    }

    private void InstallationChanged()
    {
        // Called by installation scope release. Re-evaluation is an event even
        // when this host's previous failure kind cannot be polled.
        lock (sync)
        {
            InstallationChangedLocked();
        }
        PublishAvailabilityChanges();
    }

    private void InstallationChangedLocked()
    {
        if (disposed || suspended || pendingExit is not null) return;
        if (resolveExecutablePath() is null) SetUnavailableLocked(HardwareBridgeUnavailableKind.NotInstalled, "hardware-bridge-not-installed");
        else SetAvailableLocked();
    }

    private void PublishRunFailure(Exception error, HelperErrorOutput? diagnostics)
    {
        HardwareBridgeUnavailableKind? kind = error switch
        {
            BridgeHardwareUnavailableException => HardwareBridgeUnavailableKind.HardwareUnsupported,
            Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException => HardwareBridgeUnavailableKind.AccessDenied,
            _ when diagnostics?.FailureReason == options.ErrorOutput.MissingRuntimeReason => HardwareBridgeUnavailableKind.RuntimeMissing,
            _ => null
        };
        if (kind is { } recognized)
            SetUnavailable(recognized, diagnostics?.FailureReason == options.ErrorOutput.MissingRuntimeReason ? diagnostics.FailureReason : error.Message);
    }

    // Keep notification order while invoking consumer code outside the owner lock.
    private void PublishAvailabilityChanges()
    {
        lock (sync)
        {
            if (publishingAvailability) return;
            publishingAvailability = true;
        }
        while (true)
        {
            bool value;
            lock (sync)
            {
                if (availabilityChanges.Count == 0) { publishingAvailability = false; return; }
                value = availabilityChanges.Dequeue();
            }
            try { AvailabilityChanged?.Invoke(value); }
            catch
            {
                lock (sync) publishingAvailability = false;
                throw;
            }
        }
    }

    private void DisposeAvailabilityChecks()
    {
        Task check;
        lock (sync) { CancelAvailabilityCheckLocked(); check = availabilityCheck; }
        check.GetAwaiter().GetResult();
        lock (sync)
        {
            availabilityStop?.Dispose(); availabilityStop = null;
            pendingExit?.Dispose(); pendingExit = null;
        }
    }
    private sealed class BridgeHardwareUnavailableException(string? reason) : IOException(reason ?? "HardwareBridge hardware is unavailable.") { }
}
