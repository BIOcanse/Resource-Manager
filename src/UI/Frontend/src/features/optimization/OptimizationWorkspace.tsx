import { OptimizationPage } from "./OptimizationPage";
import type { OptimizationStore } from "../../stores/optimizationStore";
import type { OptimizationReportItem } from "../../types";

interface OptimizationWorkspaceProps {
  optimization: OptimizationStore;
  onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
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
        onRefresh={() => void optimization.refreshReports(true)}
        onOptimizationModeChange={(mode) => void optimization.changeHostManagerSmartCoordinatorMode(mode)}
        onDismiss={(report) => void optimization.dismissReport(report)}
        onInspect={(report) => void props.onInspectOptimizationTarget(report)}
        onRemoveTrust={(target) => void optimization.removeTrustedTarget(target)}
    />
  );
}
