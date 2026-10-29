import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import type { ReportHistoryKind } from "../../data/reportHistory/reportHistoryClear.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { uiText } from "../../text.ts";

/** 清空历史进行中时，报告页动作状态（例如优化报告的 actionId）的值。 */
export const reportHistoryClearActionId = "report-history.clear";

/**
 * 清空历史前的确认（docs/report_lifecycle_2026-10-28.md“界面在每类报告的页面上给出这个操作，确认后调用”）：
 * 写明这类报告要删的历史条目数和保留什么；用户确认才返回 true。条数是页面当前显示的，删除的实际条数以后端回复为准。
 * 智能调度历史按页读取，页面不知道总数，确认框只说删除全部历史记录。
 */
export function confirmReportHistoryClear(
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>,
  kind: ReportHistoryKind,
  historyCount: number
): Promise<boolean> {
  const copy = uiText.reportHistory;
  return confirmDialog({
    title: copy.confirmTitle,
    message: kind === "smartRecords"
      ? copy.confirmMessage.smartRecords
      : copy.confirmMessage[kind](formatInterfaceNumber(historyCount)),
    details: [...copy.details[kind]],
    tone: "warning",
    confirmLabel: copy.confirm,
    dedupeKey: `report-history.clear.${kind}`
  });
}

/** 清空完成后的提示内容：后端回复的删除条数。 */
export function reportHistoryClearedMessage(removedCount: number) {
  return uiText.reportHistory.cleared(formatInterfaceNumber(removedCount));
}
