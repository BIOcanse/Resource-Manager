import { Match, Switch, createSignal } from "solid-js";
import type { DetailsTab } from "../../app/navigation/detailsNavigation.ts";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import { CpuBaselineRatioPanel } from "./CpuBaselineRatioPanel";
import { CpuTopologyDiagram } from "./CpuTopologyDiagram";
import { DeviceTopologyView } from "../deviceTopology/DeviceTopologyView";
import { GpuSchedulingModel } from "../gpuScheduling/GpuSchedulingModel";
import { HostManagerSmartCoordinatorDetailsReport } from "./HostManagerSmartCoordinatorDetailsReport";
import { TargetedReportsPage } from "../targetedReport/TargetedReportsPage.tsx";
import type { SoftwareRecord } from "../../types.ts";

interface DetailsPageProps {
  /** 选中的二级分页（分页树，app/navigation/detailsNavigation.ts）；分页条由外壳画。 */
  activeTab: DetailsTab;
  software: readonly SoftwareRecord[];
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
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
              <HostManagerSmartCoordinatorDetailsReport software={props.software} />
            </div>
          </Match>
          <Match when={props.activeTab === "targeted"}>
            <div class="details-resource-region"><TargetedReportsPage /></div>
          </Match>
        </Switch>
      </div>
    </section>
  );
}
