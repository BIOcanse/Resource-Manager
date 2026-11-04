import type { Accessor, JSX } from "solid-js";
import { CurrentPageNameProvider } from "../../../app/navigation/currentPageName.tsx";
import type { OptimizationStore } from "../../../stores/optimizationStore";
import type { SettingsStore } from "../../../stores/settingsStore";
import type { OptimizationReportItem, SoftwareRecord } from "../../../types";
import type { ReportHistoryClearFeedback } from "../../reportHistory/useReportHistoryClear.ts";

/** Share confirmations, result notices, storage, and availability across subpages. */
export interface ManageSubpageProps extends ReportHistoryClearFeedback {
  readonly optimization: OptimizationStore;
  readonly settings: SettingsStore;
  /** Use backend optimization-runtime availability. */
  readonly optimizationEnabled: boolean;
  readonly software: readonly SoftwareRecord[];
  /** Use the saved keepSmartRecordHistory setting to explain restored-record retention. */
  readonly keepSmartRecordHistory: boolean;
  readonly onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
}

/** Give each report section a distinct accessible heading using its feature name. */
export function ManageRecords(props: { readonly name: Accessor<string>; readonly children: JSX.Element }) {
  return (
    <div class="details-resource-region manage-records">
      <CurrentPageNameProvider name={props.name}>{props.children}</CurrentPageNameProvider>
    </div>
  );
}

/** Share report deletion and history-clear confirmations and notices. */
export function reportFeedback(props: ReportHistoryClearFeedback): ReportHistoryClearFeedback {
  return {
    confirmDialog: (request) => props.confirmDialog(request),
    onActionSucceeded: (title, message) => props.onActionSucceeded(title, message),
    onActionError: (error, fallback) => props.onActionError(error, fallback)
  };
}
