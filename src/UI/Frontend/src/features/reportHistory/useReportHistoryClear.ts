import { createSignal } from "solid-js";
import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import { clearReportHistory, type ReportHistoryKind } from "../../data/reportHistory/reportHistoryClear.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { uiText } from "../../text.ts";
import { confirmReportHistoryClear, reportHistoryClearedMessage } from "./confirmReportHistoryClear.ts";

export interface ReportHistoryClearFeedback {
  /** Use the shared application confirmation dialog. */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  /** Show the backend deletion count after success. */
  onActionSucceeded: (title: string, message: string) => void;
  /** Show the error with a generic failure explanation. */
  onActionError: (error: unknown, fallback: string) => void;
}

/** Serialize history-clear requests: confirm, request, notify, then refresh through afterClear. Page data sources own refresh failures. */
export function useReportHistoryClear(options: ReportHistoryClearFeedback & {
  kind: ReportHistoryKind;
  historyCount: () => number;
  afterClear?: () => Promise<unknown> | void;
}) {
  const runtime = useFrontendRuntime();
  const [clearing, setClearing] = createSignal(false);

  const clear = async () => {
    const historyCount = options.historyCount();
    if (clearing() || historyCount === 0) return;
    if (!await confirmReportHistoryClear(options.confirmDialog, options.kind, historyCount)) return;
    setClearing(true);
    let removedCount: number;
    try {
      removedCount = await clearReportHistory(runtime.requestClient, options.kind);
    } catch (error) {
      options.onActionError(error, uiText.reportHistory.failed);
      return;
    } finally {
      setClearing(false);
    }
    options.onActionSucceeded(uiText.reportHistory.clearedTitle, reportHistoryClearedMessage(removedCount));
    await options.afterClear?.();
  };

  return { clearing, clear };
}
