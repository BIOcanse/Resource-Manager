import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import type { ReportHistoryKind } from "../../data/reportHistory/reportHistoryClear.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { uiText } from "../../text.ts";

/** Action-state value used while history clearing is pending. */
export const reportHistoryClearActionId = "report-history.clear";

/** Require confirmation and explain retained data. Use visible counts only when complete; backend replies own actual deletion counts. */
export function confirmReportHistoryClear(
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>,
  kind: ReportHistoryKind,
  historyCount: number
): Promise<boolean> {
  const copy = uiText.reportHistory;
  // Paginated reports omit counts because the total is unknown.
  const message: string | ((count: string) => string) = copy.confirmMessage[kind];
  return confirmDialog({
    title: copy.confirmTitle,
    message: typeof message === "string" ? message : message(formatInterfaceNumber(historyCount)),
    details: [...copy.details[kind]],
    tone: "warning",
    confirmLabel: copy.confirm,
    dedupeKey: `report-history.clear.${kind}`
  });
}

/** Show the deletion count returned by the backend. */
export function reportHistoryClearedMessage(removedCount: number) {
  return uiText.reportHistory.cleared(formatInterfaceNumber(removedCount));
}
