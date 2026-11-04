import type { JSX } from "solid-js";
import { OptimizationPage } from "./OptimizationPage";
import type { OptimizationStore } from "../../stores/optimizationStore";
import type { OptimizationReportItem } from "../../types";
import type { ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";

interface OptimizationWorkspaceProps extends ReportHistoryClearFeedback {
  optimization: OptimizationStore;
  onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
  /** Place settings between runtime status and reports. */
  settings?: JSX.Element;
}

export function OptimizationWorkspace(props: OptimizationWorkspaceProps) {
  const optimization = props.optimization;
  return (
    <OptimizationPage
        overview={optimization.overview()}
        reportsObservation={optimization.reportsObservation()}
        hostManagerStatus={optimization.hostManagerStatus()}
        hostManagerObservation={optimization.hostManagerObservation()}
        optimizationMode={optimization.optimizationMode()}
        loading={optimization.loading()}
        actionId={optimization.actionId()}
        pendingOptimizationMode={optimization.pendingOptimizationMode()}
        optimizationModeApplyRemainingSeconds={optimization.optimizationModeApplyRemainingSeconds()}
        onRefresh={() => {
          void optimization.refreshReports(true);
          void optimization.refreshHostManagerStatus();
        }}
        onOptimizationModeChange={(mode) => void optimization.changeHostManagerSmartCoordinatorMode(mode)}
        onDismiss={(report) => void optimization.dismissReport(report)}
        onInspect={(report) => void props.onInspectOptimizationTarget(report)}
        onRemoveTrust={(target) => void optimization.removeTrustedTarget(target)}
        onClearHistory={() => void optimization.clearReportHistory()}
        confirmDialog={props.confirmDialog}
        onActionSucceeded={props.onActionSucceeded}
        onActionError={props.onActionError}
        settings={props.settings}
    />
  );
}
