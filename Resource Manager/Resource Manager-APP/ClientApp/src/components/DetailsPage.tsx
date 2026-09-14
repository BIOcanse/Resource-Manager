import { createEffect, createMemo, createSignal, For } from "solid-js";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import { CpuTopologyDiagram } from "./CpuTopologyDiagram";
import { DeviceTopologyView } from "./DeviceTopologyView";
import { GpuSchedulingModel } from "./GpuSchedulingModel";
import { HostManagerSmartCoordinatorDetailsReport } from "./HostManagerSmartCoordinatorDetailsReport";
import {
  TabsList,
  TabsPanel,
  TabsRoot,
  TabsTrigger
} from "../ui/primitives/Tabs.tsx";

type DetailsTab = "device" | "gpu" | "cpu" | "report";

const detailsTabs: Array<{ id: DetailsTab; label: string }> = [
  { id: "device", label: "设备管理" },
  { id: "gpu", label: "GPU 调度" },
  { id: "cpu", label: "CPU 拓扑" },
  { id: "report", label: "报告" }
];

interface DetailsPageProps {
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
}

export function DetailsPage(props: DetailsPageProps) {
  const [activeTab, setActiveTab] = createSignal<DetailsTab>("device");
  const [deviceScope, setDeviceScope] = createSignal<"external" | "internal">("external");
  const visibleTabs = createMemo(() => detailsTabs.filter((tab) =>
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
        <TabsList class="details-tabs" ariaLabel="详细信息分页">
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
