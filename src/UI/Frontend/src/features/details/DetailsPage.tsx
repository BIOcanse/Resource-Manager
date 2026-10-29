import { Match, Switch, createSignal } from "solid-js";
import type { DetailsTab } from "../../app/navigation/detailsNavigation.ts";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import { CpuBaselineRatioPanel } from "./CpuBaselineRatioPanel";
import { CpuTopologyDiagram } from "./CpuTopologyDiagram";
import { DeviceTopologyView } from "../deviceTopology/DeviceTopologyView";
import { GpuSchedulingModel } from "../gpuScheduling/GpuSchedulingModel";
import { HostManagerSmartCoordinatorDetailsReport } from "./HostManagerSmartCoordinatorDetailsReport";
import { TargetedReportsPage } from "../targetedReport/TargetedReportsPage.tsx";
import { KernelLeakReportPage } from "../kernelLeaks/KernelLeakReportPage.tsx";
import type { SoftwareRecord } from "../../types.ts";
import type { ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";

interface DetailsPageProps extends ReportHistoryClearFeedback {
  /** 选中的二级分页（分页树，app/navigation/detailsNavigation.ts）；分页条由外壳画。 */
  activeTab: DetailsTab;
  software: readonly SoftwareRecord[];
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
  /** 已保存的设置 performance.keepSmartRecordHistory：报告分页据此说明恢复后的记录是否保留。 */
  keepSmartRecordHistory: boolean;
}

export function DetailsPage(props: DetailsPageProps) {
  const [deviceScope, setDeviceScope] = createSignal<"external" | "internal">("external");

  return (
    <section id="detailsPage" class="page active-page details-page">
      <div class="optimization-advanced-layout">
        <Switch>
          <Match when={props.activeTab === "device"}>
            <div class="details-resource-region">
              <DeviceTopologyView scope={deviceScope()} onScopeRequested={setDeviceScope} />
            </div>
          </Match>
          <Match when={props.activeTab === "gpu"}>
            <div class="details-resource-region">
              <GpuSchedulingModel runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()} />
            </div>
          </Match>
          <Match when={props.activeTab === "cpu"}>
            <div class="details-resource-region">
              <CpuTopologyDiagram
                runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()}
                onOpenSoftwareSettings={props.onOpenSoftwareSettings}
              />
              <CpuBaselineRatioPanel runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()} />
            </div>
          </Match>
          <Match when={props.activeTab === "report"}>
            <div class="details-resource-region">
              <HostManagerSmartCoordinatorDetailsReport software={props.software}
                keepHistory={props.keepSmartRecordHistory} {...reportFeedback(props)} />
            </div>
          </Match>
          <Match when={props.activeTab === "targeted"}>
            <div class="details-resource-region"><TargetedReportsPage {...reportFeedback(props)} /></div>
          </Match>
          <Match when={props.activeTab === "kernelLeaks"}>
            <div class="details-resource-region"><KernelLeakReportPage {...reportFeedback(props)} /></div>
          </Match>
        </Switch>
      </div>
    </section>
  );
}

/** 报告分页用的确认和结果提示（删除录制、清空历史）。 */
function reportFeedback(props: ReportHistoryClearFeedback): ReportHistoryClearFeedback {
  return {
    confirmDialog: (request) => props.confirmDialog(request),
    onActionSucceeded: (title, message) => props.onActionSucceeded(title, message),
    onActionError: (error, fallback) => props.onActionError(error, fallback)
  };
}
