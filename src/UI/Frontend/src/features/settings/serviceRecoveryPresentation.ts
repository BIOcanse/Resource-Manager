import type { ServiceRecoveryDiagnostics } from "../../types.ts";
import type { SettingsTextBundle } from "../../i18n/settingsTypes.ts";

export function recoveryPresentation(
  diagnostics: ServiceRecoveryDiagnostics | undefined,
  text: SettingsTextBundle["systemIntegration"]
) {
  const actual = diagnostics?.actual;
  const restarts = actual?.actions.filter((action) => action.type === 1);
  const status = diagnostics?.state === "applied" ? text.crashRecoveryApplied
    : diagnostics?.state === "failed" ? text.crashRecoveryFailed
    : diagnostics?.state === "skipped" ? text.crashRecoverySkipped
    : diagnostics?.state === "mismatch" ? text.crashRecoveryMismatch
    : text.crashRecoveryUnavailable;
  return {
    status,
    resetDescription: text.crashCounterResetDescription.replace("{days}",
      diagnostics && diagnostics.configuredResetPeriodSeconds > 0
        ? String(diagnostics.configuredResetPeriodSeconds / 86400) : "—"),
    actualCount: actual?.actions.at(-1)?.type === 1 ? "∞" : restarts ? String(restarts.length) : "—",
    actualDelays: restarts?.length ? restarts.map((action) => action.delayMilliseconds / 1000).join(", ") : "—",
    actualResetDays: actual ? String(actual.resetPeriodSeconds / 86400) : "—"
  };
}
