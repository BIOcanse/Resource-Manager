namespace ResourceManager.App.Domain.Settings;

// Actual service configuration, independent of the requested preference.
public sealed record ServiceRecoveryAction(uint Type, uint DelayMilliseconds);

public sealed record ServiceRecoveryConfiguration(
    uint ResetPeriodSeconds,
    IReadOnlyList<ServiceRecoveryAction> Actions,
    bool FailureActionsOnNonCrashFailures);

public sealed record ServiceRecoveryDiagnostics(
    string State,
    int ConfiguredResetPeriodSeconds,
    ServiceRecoveryConfiguration? Actual = null,
    string? SkipReason = null,
    string? WriteError = null,
    string? ReadError = null);
