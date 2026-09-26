import { createEffect, createMemo, createSignal, For } from "solid-js";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import { CpuTopologyDiagram } from "./CpuTopologyDiagram";
import { DeviceTopologyView } from "../deviceTopology/DeviceTopologyView";
import { GpuSchedulingModel } from "../gpuScheduling/GpuSchedulingModel";
import { HostManagerSmartCoordinatorDetailsReport } from "./HostManagerSmartCoordinatorDetailsReport";
import {
  TabsList,
  TabsPanel,
  TabsRoot,
  TabsTrigger
} from "../../ui/primitives/Tabs.tsx";
import { uiText } from "../../text.ts";

type DetailsTab = "device" | "gpu" | "cpu" | "report";

// 文案按当前语言求值，不能在模块顶层固化。
function detailsTabs(): Array<{ id: DetailsTab; label: string }> {
  return [
    { id: "device", label: uiText.misc.detailsTab.device },
    { id: "gpu", label: uiText.misc.detailsTab.gpu },
    { id: "cpu", label: uiText.misc.detailsTab.cpu },
    { id: "report", label: uiText.misc.detailsTab.report }
  ];
}

interface DetailsPageProps {
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
}

export function DetailsPage(props: DetailsPageProps) {
  const [activeTab, setActiveTab] = createSignal<DetailsTab>("device");
  const [deviceScope, setDeviceScope] = createSignal<"external" | "internal">("external");
  const visibleTabs = createMemo(() => detailsTabs().filter((tab) =>
    tab.id !== "report" || props.runtimeCapabilities.optimizationEnabled()));

  createEffect(() => {
    if (activeTab() === "report" && !props.runtimeCapabilities.optimizationEnabled()) {
      setActiveTab("device");
    }
  });

  return (
    <section id="detailsPage" class="page active-page details-page">
      <TabsRoot
        value={activeTab()}
        onChange={(value) => setActiveTab(value as DetailsTab)}
      >
        <TabsList class="details-tabs" ariaLabel={uiText.misc.detailsTab.tabsLabel}>
          <For each={visibleTabs()}>
            {(tab) => (
              <TabsTrigger value={tab.id} class="details-tab">
                {tab.label}
              </TabsTrigger>
            )}
          </For>
        </TabsList>
        <div class="optimization-advanced-layout">
          <TabsPanel value="device">
          <div class="details-resource-region">
            <DeviceTopologyView scope={deviceScope()} onScopeRequested={setDeviceScope} />
          </div>
          </TabsPanel>
          <TabsPanel value="gpu">
          <div class="details-resource-region">
            <GpuSchedulingModel runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()} />
          </div>
          </TabsPanel>
          <TabsPanel value="cpu">
          <div class="details-resource-region">
            <CpuTopologyDiagram
              runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()}
              onOpenSoftwareSettings={props.onOpenSoftwareSettings}
            />
          </div>
          </TabsPanel>
          <TabsPanel value="report">
          <div class="details-resource-region">
            <HostManagerSmartCoordinatorDetailsReport />
          </div>
          </TabsPanel>
        </div>
      </TabsRoot>
    </section>
  );
}
