import { createSignal } from "solid-js";
import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import { clearReportHistory, type ReportHistoryKind } from "../../data/reportHistory/reportHistoryClear.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { uiText } from "../../text.ts";
import { confirmReportHistoryClear, reportHistoryClearedMessage } from "./confirmReportHistoryClear.ts";

export interface ReportHistoryClearFeedback {
  /** 应用统一的确认对话框。 */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  /** 清空完成：标题和后端回复的删除条数。 */
  onActionSucceeded: (title: string, message: string) => void;
  /** 清空失败：错误和通用说明。 */
  onActionError: (error: unknown, fallback: string) => void;
}

/**
 * 详细信息里各报告分页的“清空历史”：确认 → 请求 → 提示删除条数 → afterClear（页面按自己的数据源更新，
 * 读取失败由页面自己的读取状态显示）。clearing 在请求期间为 true，页面用它禁用按钮；同一时间只发一个请求。
 */
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
