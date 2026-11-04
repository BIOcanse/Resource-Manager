import { Match, Switch, createSignal } from "solid-js";
import type { DetailsTab } from "../../app/navigation/detailsNavigation.ts";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import { CpuBaselineRatioPanel } from "./CpuBaselineRatioPanel";
import { CpuTopologyDiagram } from "./CpuTopologyDiagram";
import { DeviceTopologyView } from "../deviceTopology/DeviceTopologyView";
import { GpuSchedulingModel } from "../gpuScheduling/GpuSchedulingModel";

/* Device information is a view over device, GPU, and processor subpages. */
interface DetailsPageProps {
  /** Selected secondary page; the shell renders its navigation. */
  activeTab: DetailsTab;
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
        </Switch>
      </div>
    </section>
  );
}
