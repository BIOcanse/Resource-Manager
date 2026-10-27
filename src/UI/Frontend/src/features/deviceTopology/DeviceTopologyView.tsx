import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { createEffect, createMemo, createSignal, createUniqueId, For, Index, Show } from "solid-js";
import {
  Bluetooth,
  BatteryCharging,
  Cable,
  Camera,
  CircleHelp,
  Cpu,
  EthernetPort,
  HardDrive,
  Headphones,
  Keyboard,
  Laptop,
  MemoryStick,
  Monitor,
  Mouse,
  Power,
  Radio,
  Smartphone,
  Tablet,
  Usb,
  Zap
} from "lucide-solid";
import { describeDeviceTopologyNode } from "./deviceTopologyPresentation";
import {
  buildExternalInterfaceTree,
  filterExternalInterfaceTree
} from "./externalInterfaceTree";
import {
  buildInternalInterfaceTree,
  filterInternalInterfaceTree
} from "./internalInterfaceTree";
import {
  attachedDisplay,
  deviceUserDetailSections,
  isDeviceRole,
  nodeHeadline,
  nodeSummaryFields,
  type DeviceTopologyListNode,
  type DeviceTopologyScope
} from "./deviceDetailSections.ts";
import { useDeviceTopologyState } from "./deviceTopologyStore";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";

import { deviceMetricFieldNames } from "./deviceMetricFields.ts";
import { DisplayEdidDetails } from "./components/DisplayEdidDetails.tsx";
import { DeviceSpecializedDetails } from "./components/DeviceSpecializedDetails";
import { DeviceMetricFieldDetails } from "./components/DeviceMetricFieldDetails";
import { DeviceSpecializedSummary } from "./components/DeviceSpecializedSummary";
import { DeviceMachineView } from "./components/DeviceMachineView";
import { ObservationStateNotice } from "../../components/ObservationStateNotice";
import { UserDetailsDialog } from "../../components/UserDetailsDialog";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { uiText } from "../../text.ts";

export type { DeviceTopologyScope };

interface DeviceTopologyViewProps {
  scope: DeviceTopologyScope;
  onScopeRequested?: (scope: DeviceTopologyScope) => void;
}

export function DeviceTopologyView(props: DeviceTopologyViewProps) {
  const demandId = "details.device-topology";
  const headingId = createUniqueId();
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsDeviceTopology]);
  const topology = useDeviceTopologyState();
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const frontendWork = useFrontendWork();
  // Device fields share the topology view demand, independently of its dialog.
  const metricFieldSource = usePushSource(runtime.sources.metricFields, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsDeviceTopology),
    intervalMs: readIntervals.current()?.monitor,
    query: { fields: deviceMetricFieldNames }
  })).snapshot;
  const metricFieldDevices = () => metricFieldSource().data;
  const snapshot = createMemo(() => topology.state().snapshot ?? null);
  const [selectedNodeId, setSelectedNodeId] = createSignal<string | null>(null);
  const [detailsOpen, setDetailsOpen] = createSignal(false);
  const [searchQuery, setSearchQuery] = createSignal("");
  const ports = createMemo(() => snapshot()?.ports ?? []);
  const externalTree = createMemo(() => buildExternalInterfaceTree(ports()));
  const internalTree = createMemo(() => buildInternalInterfaceTree(ports(), externalTree()));
  const sectionNodes = createMemo<DeviceTopologyListNode[]>(() => {
    return props.scope === "external" ? externalTree().nodes : internalTree().nodes;
  });
  const visibleNodes = createMemo<DeviceTopologyListNode[]>(() => {
    const query = searchQuery().trim().toLocaleLowerCase();
    return props.scope === "external"
      ? filterExternalInterfaceTree(externalTree().nodes, query)
      : filterInternalInterfaceTree(internalTree().nodes, query);
  });
  // 每次推送都会重建节点对象；清单按节点 id 渲染，同一节点的按钮在推送之间保持不变，
  // 否则整张清单每次都被删除重建，带焦点的按钮被删掉时清单会跳回顶部。
  const visibleNodeIds = createMemo(() => visibleNodes().map((node) => node.id));
  const visibleNodeById = createMemo(() => new Map(visibleNodes().map((node) => [node.id, node])));
  const selectedNode = createMemo(() => {
    const selected = selectedNodeId();
    return visibleNodes().find((node) => node.id === selected) ?? visibleNodes()[0] ?? null;
  });
  const selectedPort = createMemo(() => {
    return selectedNode()?.port ?? null;
  });
  const selectedPortPresentation = createMemo(() => {
    const port = selectedPort();
    return port ? describeDeviceTopologyNode(port) : null;
  });
  const selectedParentNode = createMemo(() => {
    const parentId = selectedNode()?.parentId;
    return parentId ? sectionNodes().find((node) => node.id === parentId) ?? null : null;
  });
  const selectedNodeChildCount = createMemo(() => {
    const selected = selectedNode()?.id;
    return selected ? sectionNodes().filter((node) => node.parentId === selected).length : 0;
  });
  const selectedPortSummary = createMemo(() => {
    const node = selectedNode();
    return node ? nodeSummaryFields(node, selectedNodeChildCount(), props.scope) : [];
  });
  const selectedDetailSections = createMemo(() => {
    const port = selectedPort();
    const node = selectedNode();
    if (!port || !node) {
      return [];
    }

    return deviceUserDetailSections(
      port,
      node,
      selectedParentNode(),
      props.scope,
      selectedNodeChildCount(),
      selectedPortSummary());
  });
  const scopeTitle = () => props.scope === "external" ? uiText.deviceTopology.externalScope : uiText.deviceTopology.internalScope;
  const scopeStatistics = () => props.scope === "external"
    ? uiText.deviceTopology.externalSummary(externalTree().hostInterfaceCount, externalTree().downstreamInterfaceCount)
    : uiText.deviceTopology.internalSummary(internalTree().interfaceCount, internalTree().controllerCount, internalTree().attachedDeviceCount);

  createEffect(() => {
    const requested = topology.requestedPortId();
    const requestedPort = requested ? ports().find((port) => port.id === requested) : undefined;
    if (requestedPort) {
      const externalNode = externalTree().nodes.find((node) => node.port.id === requested && node.role !== "attached-device")
        ?? externalTree().nodes.find((node) => node.port.id === requested);
      const internalNode = internalTree().nodes.find((node) => node.port.id === requested && node.role !== "internal-interface");
      const requestedScope: DeviceTopologyScope = externalNode ? "external" : "internal";
      if (requestedScope !== props.scope) {
        props.onScopeRequested?.(requestedScope);
        return;
      }
      const requestedNode = props.scope === "external" ? externalNode : internalNode;
      setSearchQuery("");
      setSelectedNodeId(requestedNode?.id ?? null);
      topology.clearRequestedPort();
      return;
    }

    const current = selectedNodeId();
    const items = visibleNodes();
    if (items.length === 0) {
      setSelectedNodeId(null);
      return;
    }

    if (!current || !items.some((node) => node.id === current)) {
      setSelectedNodeId(items[0].id);
    }
  });

  return (
    <section
      {...frontendVisibilitySurface("visible.details.device-topology.surface", [demandId])}
      class="device-topology-panel"
      aria-labelledby={headingId}
    >
      {/* 所在分页已经叫“设备拓扑”（分页树 2026-10-26），看得见的标题不再重复；标题只留给读屏，
          区域顶部是整机的识别名。 */}
      <header class="device-topology-header">
        <h2 class="sr-only" id={headingId}>{uiText.deviceTopology.panel}</h2>
        <span class="device-topology-identity">{snapshot()?.system.brandDisplayName ?? uiText.deviceTopology.brandLoading}</span>
      </header>

      <ObservationStateNotice
        state={topology.observation()}
        label={uiText.deviceTopology.panel}
      />

      <Show
        when={snapshot()}
        fallback={
          <div class="device-topology-loading">
            {topology.observation().status === "error" ? uiText.deviceTopology.unavailable : uiText.deviceTopology.loading}
          </div>
        }
      >
        {(data) => (
            <div class="device-topology-layout">
              <aside class="device-port-list" aria-label={uiText.deviceTopology.listLabel(scopeTitle())}>
                {/* 左栏标题就是内外切换（用户 2026-10-25）：选中的一项说明当前清单，不再另写一遍。 */}
                <div class="device-port-list-title">
                  <SegmentedControl
                    value={props.scope}
                    ariaLabel={uiText.deviceTopology.scopeNav}
                    class="device-scope-tabs"
                    itemClass="device-scope-tab"
                    options={[
                      { id: "external", label: uiText.deviceTopology.externalScope },
                      { id: "internal", label: uiText.deviceTopology.internalScope }
                    ]}
                    onChange={(scope) => props.onScopeRequested?.(scope)}
                  />
                  <span>
                    {!searchQuery().trim()
                      ? scopeStatistics()
                      : visibleNodes().length === sectionNodes().length
                        ? uiText.deviceTopology.itemCount(sectionNodes().length)
                        : `${visibleNodes().length} / ${sectionNodes().length}`}
                  </span>
                </div>
                <input
                  class="device-port-search"
                  type="search"
                  value={searchQuery()}
                  onInput={(event) => setSearchQuery(event.currentTarget.value)}
                  placeholder={uiText.deviceTopology.searchPlaceholder(scopeTitle())}
                  aria-label={uiText.deviceTopology.searchPlaceholder(scopeTitle())}
                />
                <Show
                  when={visibleNodes().length > 0}
                  fallback={<p class="device-topology-empty">{sectionNodes().length > 0 ? uiText.deviceTopology.noMatch : uiText.deviceTopology.noneFound(scopeTitle())}</p>}
                >
                  <For each={visibleNodeIds()}>
                    {(nodeId) => {
                      const node = () => visibleNodeById().get(nodeId)!;
                      return (
                        <button
                          type="button"
                          class="device-port-item"
                          classList={{
                            active: selectedNode()?.id === nodeId,
                            disconnected: node().connectionState === "disconnected",
                            nested: node().depth > 0,
                            "device-node": isDeviceRole(node().role)
                          }}
                          aria-pressed={selectedNode()?.id === nodeId}
                          style={`--device-tree-depth:${Math.min(node().depth, 8)}`}
                          onClick={() => setSelectedNodeId(nodeId)}
                          data-full-content={node().subtitle
                            ? `${node().path?.join(" → ") ?? node().title} · ${node().subtitle}`
                            : node().path?.join(" → ") ?? node().title}
                        >
                          <DeviceConnectorIcon kind={node().iconKind ?? node().connectorKind} />
                          <span class="device-port-main">
                            {/* 标签和标题同一行；放不下时标签换到标题下面，标签文字从不截断（协调者 10-26）。 */}
                            <span class="device-port-heading">
                              <strong>{node().title}</strong>
                              <span
                                class="device-port-protocol"
                                classList={{
                                  connected: node().connectionState === "connected",
                                  disconnected: node().connectionState === "disconnected"
                                }}
                              >
                                {node().badge}
                              </span>
                            </span>
                            <small>{node().subtitle}</small>
                          </span>
                        </button>
                      );
                    }}
                  </For>
                </Show>
              </aside>

              <DeviceMachineView system={data().system} machineIdentity={data().machineIdentity} gpuInformation={data().gpuInformation} externalMachines={data().externalMachines} />

              <aside class="device-port-detail" aria-label={uiText.deviceTopology.detailPane}>
                <Show
                  when={selectedPort()}
                  fallback={<p class="device-topology-empty">{uiText.deviceTopology.selectPrompt}</p>}
                >
                  {(port) => (
                    <>
                      <div class="device-port-detail-title">
                        <DeviceConnectorIcon kind={selectedNode()?.iconKind ?? selectedNode()?.connectorKind ?? port().connectorKind} large />
                        <div>
                          <strong>{selectedNode()?.title ?? selectedPortPresentation()?.title ?? port().displayName}</strong>
                          <span>{nodeHeadline(selectedNode()!, props.scope)}</span>
                          </div>
                      </div>
                      <Show when={selectedNode()?.path && (selectedNode()?.path?.length ?? 0) > 1}>
                        <div class="device-relation-path" role="group" aria-label={uiText.deviceTopology.relationPath}>
                          <For each={selectedNode()?.path ?? []}>
                            {(segment) => <span>{segment}</span>}
                          </For>
                        </div>
                      </Show>
                      <Show
                        when={selectedNode()?.specializedDevice}
                        fallback={
                          <div class="device-detail-summary" role="group" aria-label={uiText.deviceTopology.keyFacts}>
                            <Index each={selectedPortSummary()}>
                              {(field) => (
                                <div class="device-detail-summary-item">
                                  <span>{field().label}</span>
                                  <strong>{field().value}</strong>
                                </div>
                              )}
                            </Index>
                          </div>
                        }
                      >
                        {(model) => <DeviceSpecializedSummary model={model()} />}
                      </Show>
                      <button class="secondary details-button device-details-button" type="button" onClick={() => setDetailsOpen(true)}>
                        {uiText.deviceTopology.allInformation}
                      </button>
                    </>
                  )}
                </Show>
              </aside>

              <Show when={(data().notes ?? []).length > 0 || topology.state().attemptDiagnostics.length > 0}>
                <div class="device-topology-notes">
                  <span>{uiText.deviceTopology.partialUnavailable}</span>
                </div>
              </Show>
            </div>
        )}
      </Show>
      <UserDetailsDialog
        open={detailsOpen() && Boolean(selectedPort())}
        title={uiText.deviceTopology.detailTitle(selectedNode()?.title ?? uiText.deviceTopology.fallbackDeviceName)}
        sections={selectedDetailSections()}
        onClose={() => setDetailsOpen(false)}
      >
        <Show when={selectedNode()?.specializedDevice}>
          {(model) => <DeviceSpecializedDetails model={model()} />}
        </Show>
        <Show when={selectedPort()?.deviceId}>
          {(deviceId) => <DeviceMetricFieldDetails devices={metricFieldDevices()} deviceId={deviceId()} />}
        </Show>
        <Show when={attachedDisplay(selectedPort())}>
          {(display) => <DisplayEdidDetails display={display()} />}
        </Show>
      </UserDetailsDialog>
    </section>
  );
}

function DeviceConnectorIcon(props: { kind: string; large?: boolean }) {
  const size = () => props.large ? 24 : 18;
  const iconProps = { "aria-hidden": true, size: size(), strokeWidth: 1.8 };
  const icon = () => {
    switch (props.kind) {
      case "usb-a":
      case "usb-c":
      case "usb-internal":
        return <Usb {...iconProps} />;
      case "thunderbolt":
        return <Zap {...iconProps} />;
      case "hdmi":
      case "displayport":
      case "mini-displayport":
      case "dvi":
      case "vga":
      case "internal-display":
      case "monitor":
        return <Monitor {...iconProps} />;
      case "wireless-display":
        return <Radio {...iconProps} />;
      case "rj45":
      case "network-adapter":
        return <EthernetPort {...iconProps} />;
      case "audio":
        return <Headphones {...iconProps} />;
      case "power":
        return <Power {...iconProps} />;
      case "power-input":
        return <BatteryCharging {...iconProps} />;
      case "sd-card":
        return <MemoryStick {...iconProps} />;
      case "hard-drive":
      case "storage":
        return <HardDrive {...iconProps} />;
      case "keyboard":
        return <Keyboard {...iconProps} />;
      case "mouse":
        return <Mouse {...iconProps} />;
      case "camera":
        return <Camera {...iconProps} />;
      case "smartphone":
        return <Smartphone {...iconProps} />;
      case "tablet":
        return <Tablet {...iconProps} />;
      case "laptop":
        return <Laptop {...iconProps} />;
      case "headphones":
        return <Headphones {...iconProps} />;
      case "pcie":
      case "external-gpu":
      case "graphics-card":
      case "controller":
      case "acpi":
      case "internal":
        return <Cpu {...iconProps} />;
      case "bluetooth":
      case "bluetooth-device":
        return <Bluetooth {...iconProps} />;
      case "cable":
      case "dock":
        return <Cable {...iconProps} />;
      case "usb-hub":
        return <Usb {...iconProps} />;
      case "device":
        return <CircleHelp {...iconProps} />;
      default:
        return <CircleHelp {...iconProps} />;
    }
  };

  return (
    <span
      class="device-port-icon"
      classList={{ large: props.large === true, disconnected: props.kind === "unknown" }}
      aria-hidden="true"
    >
      {icon()}
    </span>
  );
}

