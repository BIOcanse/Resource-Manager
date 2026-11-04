import { createSignal, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { deviceRestartActionKind, restartReportDevice, type DeviceRestartReply } from "../../data/deviceActions/deviceActions.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import type { OptimizationReportAction, OptimizationReportItem } from "../../types.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import type { ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { uiText } from "../../text.ts";
import { deviceRestartReasonText, deviceResultText, deviceWin32DetailText } from "./deviceActionsText.ts";

/* Show only backend-suggested restarts. Each request requires explicit confirmation of backend risk codes; display the returned outcome codes. */

/** Return the suggested restart action, or null when unavailable. */
export function deviceRestartActionOf(report: OptimizationReportItem): OptimizationReportAction | null {
  return report.suggestedActions.find(action => action.kind === deviceRestartActionKind) ?? null;
}

export interface DeviceRestartOutcome {
  readonly kind: "done" | "refused" | "failed";
  readonly lines: readonly string[];
}

function replyLines(reply: DeviceRestartReply): string[] {
  const copy = uiText.deviceActions.restart;
  const detail = deviceWin32DetailText(reply.detail);
  return [
    copy.reason(deviceRestartReasonText(reply.reasonCode)),
    copy.deviceResult(deviceResultText(reply.deviceResultCode)),
    ...detail === null ? [] : [copy.win32Detail(detail)],
    reply.sequence === null ? copy.notRecorded : copy.recorded(formatInterfaceNumber(reply.sequence))
  ];
}

/** Keep busy state and the latest outcome local to each report card. */
export function createDeviceRestart(feedback: ReportHistoryClearFeedback) {
  const runtime = useFrontendRuntime();
  const [busy, setBusy] = createSignal(false);
  const [outcome, setOutcome] = createSignal<DeviceRestartOutcome | null>(null);

  const run = async (report: OptimizationReportItem, action: OptimizationReportAction, target: string) => {
    if (busy()) return;
    const copy = uiText.deviceActions.restart;
    const risks = action.riskCodes.map(code => Object.hasOwn(copy.risks, code)
      ? copy.risks[code as keyof typeof copy.risks] : copy.unknownRisk(code));
    const confirmed = await feedback.confirmDialog({
      title: copy.confirmTitle,
      message: copy.confirmMessage(target),
      details: [...copy.confirmDetails, ...risks],
      tone: "warning",
      confirmLabel: copy.confirm,
      dedupeKey: `device-restart:${report.id}`
    });
    if (!confirmed) return;
    setBusy(true);
    setOutcome(null);
    try {
      const reply = await restartReportDevice(runtime.requestClient, report.id);
      const lines = replyLines(reply);
      const done = reply.reasonCode === 0;
      setOutcome({ kind: done ? "done" : "refused", lines });
      if (done) feedback.onActionSucceeded(copy.doneTitle, lines.join(" "));
    } catch (error) {
      setOutcome({ kind: "failed", lines: [userFacingErrorMessage(error, copy.failed)] });
      feedback.onActionError(error, copy.failed);
    } finally {
      setBusy(false);
    }
  };

  return { busy, outcome, run };
}

export function DeviceRestartButton(props: { busy: boolean; disabled: boolean; onClick: () => void }) {
  const copy = () => uiText.deviceActions.restart;
  return (
    <button class="secondary device-restart-button" type="button" data-full-content={copy().hint} aria-description={copy().hint}
      disabled={props.disabled || props.busy} onClick={() => props.onClick()}>
      <StableLabel labels={{ idle: copy().label, busy: copy().busy }} current={props.busy ? "busy" : "idle"} />
    </button>
  );
}

/** Show the outcome heading and returned codes. */
export function DeviceRestartResult(props: { outcome: DeviceRestartOutcome | null }) {
  const copy = () => uiText.deviceActions.restart;
  return (
    <Show when={props.outcome}>
      {(outcome) => (
        <div class="device-restart-result" data-outcome={outcome().kind} role={outcome().kind === "done" ? "status" : "alert"}>
          <strong>{outcome().kind === "done" ? copy().doneTitle : outcome().kind === "refused" ? copy().refusedTitle : copy().failed}</strong>
          {outcome().lines.map(line => <span>{line}</span>)}
        </div>
      )}
    </Show>
  );
}
