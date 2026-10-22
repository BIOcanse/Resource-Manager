import { displayConnectorTechnology, portDisplayName, portHardwareKind } from "./deviceVocabulary.ts";
import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
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
import {
  describeDeviceTopologyNode,
  deviceTopologySummaryFields
} from "./deviceTopologyPresentation";
import {
  buildExternalInterfaceTree,
  filterExternalInterfaceTree,
  type ExternalInterfaceNodeRole
} from "./externalInterfaceTree";
import {
  buildInternalInterfaceTree,
  filterInternalInterfaceTree,
  type InternalInterfaceNodeRole
} from "./internalInterfaceTree";
import type { SpecializedDeviceModel } from "./adapters/adapterRegistry";
import { useDeviceTopologyState } from "./deviceTopologyStore";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import type { MetricFieldDevice } from "../../data/metricFields/metricFieldSource.ts";
import { deviceMetricFieldNames } from "./deviceMetricFields.ts";
import type { DeviceTopologyPort, DeviceTopologyUsbConnection } from "../../types";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection,
  type UserDetailItem
} from "../../presentation/userDetails";
import { DisplayEdidDetails } from "./components/DisplayEdidDetails.tsx";
import { DeviceSpecializedDetails } from "./components/DeviceSpecializedDetails";
import { DeviceMetricFieldDetails } from "./components/DeviceMetricFieldDetails";
import { DeviceSpecializedSummary } from "./components/DeviceSpecializedSummary";
import { ObservationStateNotice } from "../../components/ObservationStateNotice";
import { UserDetailsDialog } from "../../components/UserDetailsDialog";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import { uiText } from "../../text.ts";

interface DeviceTopologyListNode {
  id: string;
  port: DeviceTopologyPort;
  title: string;
  subtitle: string;
  badge: string;
  connectorKind: string;
  iconKind?: string;
  connectionState?: "connected" | "disconnected" | "unknown";
  depth: number;
  role?: DeviceTopologyNodeRole;
  parentId?: string;
  path?: string[];
  specializedDevice?: SpecializedDeviceModel;
}

export type DeviceTopologyScope = "external" | "internal";
type DeviceTopologyNodeRole = ExternalInterfaceNodeRole | InternalInterfaceNodeRole;

interface DeviceTopologyViewProps {
  scope: DeviceTopologyScope;
  onScopeRequested?: (scope: DeviceTopologyScope) => void;
}

export function DeviceTopologyView(props: DeviceTopologyViewProps) {
  const demandId = "details.device-topology";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsDeviceTopology]);
  const topology = useDeviceTopologyState();
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const [metricFieldDevices, setMetricFieldDevices] = createSignal<readonly MetricFieldDevice[] | null>(null);
  // NPU／AMD NPU 实时数值和设备拓扑同一生命周期订阅：所有订阅共用一条推送流，
  // 跟着弹窗开关订阅会让整条流（含设备拓扑）反复重连。
  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsDeviceTopology)) {
      return;
    }
    onCleanup(runtime.sources.metricFields.subscribe({ fields: deviceMetricFieldNames }, 1_000, setMetricFieldDevices));
  });
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
  const selectedNodeIsInterface = createMemo(() => isInterfaceRole(selectedNode()?.role));
  const selectedNodeChildCount = createMemo(() => {
    const selected = selectedNode()?.id;
    return selected ? sectionNodes().filter((node) => node.parentId === selected).length : 0;
  });
  const selectedPortSummary = createMemo(() => {
    const port = selectedPort();
    if (!port) {
      return [];
    }

    if (selectedNodeIsInterface()) {
      return interfaceSummaryFields(selectedNode()!, selectedNodeChildCount(), props.scope);
    }
    return selectedNode()?.specializedDevice?.summaryFields ?? deviceTopologySummaryFields(port);
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
    >
      <header class="device-topology-header">
        <div class="device-topology-heading">
          <h2>{uiText.deviceTopology.panel}</h2>
          <span>{snapshot()?.system.brandDisplayName ?? uiText.deviceTopology.brandLoading}</span>
        </div>
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
      </header>

      <ObservationStateNotice
        state={topology.observation()}
        label={uiText.deviceTopology.panel}
      />

      <Show
        when={snapshot()}
        fallback={
          <div class="device-topology-loading">
            {topology.state().state === "failed" ? uiText.deviceTopology.unavailable : uiText.deviceTopology.loading}
          </div>
        }
      >
        {(data) => (
            <div class="device-topology-layout">
              <aside class="device-port-list" aria-label={uiText.deviceTopology.listLabel(scopeTitle())}>
                <div class="device-port-list-title">
                  <strong>{scopeTitle()}</strong>
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
                  <For each={visibleNodes()}>
                    {(node) => (
                        <button
                          type="button"
                          class="device-port-item"
                          classList={{
                            active: selectedNode()?.id === node.id,
                            disconnected: node.connectionState === "disconnected",
                            nested: node.depth > 0,
                            "device-node": isDeviceRole(node.role)
                          }}
                          aria-pressed={selectedNode()?.id === node.id}
                          style={`--device-tree-depth:${Math.min(node.depth, 8)}`}
                          onClick={() => setSelectedNodeId(node.id)}
                          title={node.path?.join(" → ") ?? (node.subtitle ? `${node.title} · ${node.subtitle}` : node.title)}
                        >
                          <DeviceConnectorIcon kind={node.iconKind ?? node.connectorKind} />
                          <span class="device-port-main">
                            <strong>{node.title}</strong>
                            <small>{node.subtitle}</small>
                          </span>
                          <span
                            class="device-port-protocol"
                            classList={{
                              connected: node.connectionState === "connected",
                              disconnected: node.connectionState === "disconnected"
                            }}
                          >
                            {node.badge}
                          </span>
                        </button>
                    )}
                  </For>
                </Show>
              </aside>

              <div class="device-top-view" aria-label={uiText.deviceTopology.topView}>
                <div class="device-laptop">
                  <div class="device-laptop-lid">
                    <span class="device-brand-logo">{data().system.brandLogoText}</span>
                    <strong>{data().system.manufacturer}</strong>
                    <small>{data().system.model}</small>
                  </div>
                  <div class="device-laptop-hinge" />
                  <div class="device-laptop-base">
                    <span>CPU</span>
                    <span>GPU</span>
                    <span>MEM</span>
                    <span>IO</span>
                  </div>
                </div>
                <div class="device-system-meta">
                  <span>{data().system.baseBoardManufacturer ?? uiText.deviceTopology.baseBoardManufacturerUnknown}</span>
                  <span>{data().system.baseBoardProduct ?? uiText.deviceTopology.baseBoardProductUnknown}</span>
                  <span>{data().system.biosVersion ?? uiText.deviceTopology.biosUnknown}</span>
                </div>
              </div>

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
                          <span>{selectedNode()?.subtitle || selectedPortPresentation()?.subtitle || port().hardwareKind}</span>
                          </div>
                      </div>
                      <Show when={selectedNode()?.path && (selectedNode()?.path?.length ?? 0) > 1}>
                        <div class="device-relation-path" aria-label={uiText.deviceTopology.relationPath}>
                          <For each={selectedNode()?.path ?? []}>
                            {(segment) => <span>{segment}</span>}
                          </For>
                        </div>
                      </Show>
                      <Show
                        when={selectedNode()?.specializedDevice}
                        fallback={
                          <div class="device-detail-summary" aria-label={uiText.deviceTopology.keyFacts}>
                            <For each={selectedPortSummary()}>
                              {(field) => (
                                <div class="device-detail-summary-item">
                                  <span>{field.label}</span>
                                  <strong>{field.value}</strong>
                                </div>
                              )}
                            </For>
                          </div>
                        }
                      >
                        {(model) => <DeviceSpecializedSummary model={model()} />}
                      </Show>
                      <button class="secondary details-button device-details-button" type="button" onClick={() => setDetailsOpen(true)}>
                        {uiText.deviceTopology.details}
                      </button>
                    </>
                  )}
                </Show>
              </aside>

              <Show when={(data().notes ?? []).length > 0}>
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
        <Show when={selectedPort()?.display}>
          {(display) => <DisplayEdidDetails display={display()} />}
        </Show>
      </UserDetailsDialog>
    </section>
  );
}

function deviceUserDetailSections(
  port: DeviceTopologyPort,
  node: DeviceTopologyListNode,
  parent: DeviceTopologyListNode | null,
  scope: DeviceTopologyScope,
  childCount: number,
  summary: readonly { label: string; value: string }[])
{
  const isDevice = isDeviceRole(node.role);
  const isInterface = isInterfaceRole(node.role);
  const display = port.display;
  const network = port.network;
  const usb = port.usb;
  // 概览先列和侧栏相同的关键信息；概览、连接两节里同一名称只出现一次，先出现的为准。
  const shownLabels = new Set<string>();
  const firstShown = (item: UserDetailItem | null) => {
    if (!item || shownLabels.has(item.label)) {
      return null;
    }
    shownLabels.add(item.label);
    return item;
  };

  return compactUserDetailSections([
    userDetailSection(uiText.deviceTopology.section.overview, [
      ...summary
        .filter((field) => isReportedDeviceValue(field.value))
        .map((field) => firstShown(userDetailItem(field.label, field.value))),
      firstShown(userDetailItem(uiText.deviceTopology.label.category, isDevice ? uiText.deviceTopology.connectedDevice : scope === "external" ? uiText.deviceTopology.externalScope : uiText.deviceTopology.internalScope)),
      firstShown(userDetailItem(isDevice ? uiText.deviceTopology.label.deviceType : uiText.deviceTopology.label.interfaceType, isDevice
        ? node.specializedDevice?.deviceTypeLabel ?? hardwareKindLabel(port)
        : connectorLabel(port.connectorKind))),
      firstShown(userDetailItem(uiText.deviceTopology.label.connectionState, connectionStateLabel(node.connectionState))),
      parent ? firstShown(userDetailItem(uiText.deviceTopology.label.connectedTo, parent.title)) : null,
      isInterface ? firstShown(userDetailItem(uiText.deviceTopology.label.directChildren, uiText.deviceTopology.countSuffix(childCount))) : null
    ]),
    userDetailSection(uiText.deviceTopology.section.connection, [
      firstShown(userDetailItem(uiText.deviceTopology.label.bus, reportedDeviceValue(busLabel(port.busKind)))),
      firstShown(userDetailItem(isDevice ? uiText.deviceTopology.label.currentDeviceSpeed : uiText.deviceTopology.label.currentInterfaceSpeed, reportedDeviceValue(port.speed))),
      firstShown(userDetailItem(uiText.deviceTopology.label.maxSpeed, reportedDeviceValue(port.physicalMaximumSpeed))),
      firstShown(userDetailItem(uiText.deviceTopology.label.protocol, reportedDeviceValue(isInterface ? interfaceProtocol(node, scope) : port.protocol))),
      firstShown(userDetailItem(uiText.deviceTopology.label.manufacturer, reportedDeviceValue(port.manufacturer))),
      port.advancedInterconnect
        ? firstShown(userDetailItem(uiText.deviceTopology.label.interconnectTechnology, reportedDeviceValue(port.advancedInterconnect.technology)))
        : null
    ]),
    display ? userDetailSection(uiText.deviceTopology.section.display, [
      userDetailItem(uiText.deviceTopology.label.deviceName, reportedDeviceValue(display.monitorName)),
      userDetailItem(uiText.deviceTopology.label.activeResolution, reportedDeviceValue(display.resolution)),
      userDetailItem(uiText.deviceTopology.label.refreshRate, reportedDeviceValue(display.refreshRate)),
      userDetailItem(uiText.deviceTopology.label.connectionLocation, display.internal ? uiText.deviceTopology.displayLocation.internal : uiText.deviceTopology.displayLocation.external)
    ]) : null,
    network ? userDetailSection(uiText.deviceTopology.section.network, [
      userDetailItem(uiText.deviceTopology.label.networkInterface, reportedDeviceValue(network.interfaceName)),
      userDetailItem(uiText.deviceTopology.label.connectionState, userFacingNetworkState(network.connectionState)),
      userDetailItem(uiText.deviceTopology.label.transmitSpeed, reportedDeviceValue(network.transmitLinkSpeed)),
      userDetailItem(uiText.deviceTopology.label.receiveSpeed, reportedDeviceValue(network.receiveLinkSpeed)),
      network.activeMtuBytes ? userDetailItem("MTU", `${network.activeMtuBytes} B`) : null,
      userDetailItem(uiText.deviceTopology.label.macAddress, reportedDeviceValue(network.permanentAddress))
    ]) : null,
    usb ? userDetailSection("USB", [
      userDetailItem(uiText.deviceTopology.label.port, formatUsbPort(usb)),
      userDetailItem(uiText.deviceTopology.label.usbConnection, renderBackendMessage(usb.connectionStatus)),
      userDetailItem(uiText.deviceTopology.label.connector, formatUsbConnector(usb)),
      userDetailItem(uiText.deviceTopology.label.supportedProtocols, reportedDeviceValue(usb.supportedProtocols)),
      userDetailItem(uiText.deviceTopology.label.portCapability, reportedDeviceValue(formatUsbCapability(usb))),
      userDetailItem(uiText.deviceTopology.label.deviceSpecification, reportedDeviceValue(usb.deviceSpecification)),
      userDetailItem(uiText.deviceTopology.label.deviceRevision, reportedDeviceValue(usb.deviceRevision)),
      userDetailItem(uiText.deviceTopology.label.usbClass, reportedDeviceValue(usb.deviceClass)),
      userDetailItem("VID / PID", reportedDeviceValue(formatUsbVidPid(usb))),
      userDetailItem(uiText.deviceTopology.label.serialNumber, reportedDeviceValue(usb.serialNumber))
    ]) : null,
    isDevice ? userDetailSection(uiText.deviceTopology.section.deviceIdentity, [
      userDetailItem(uiText.deviceTopology.label.hardwareIdentity, reportedDeviceValue(port.deviceId))
    ]) : null
  ]);
}

function reportedDeviceValue(value?: string | null) {
  return isReportedDeviceValue(value) ? String(value).trim() : "";
}

function isReportedDeviceValue(value?: string | null) {
  const text = String(value ?? "").trim();
  return Boolean(text && text !== "--" && text.toLocaleLowerCase() !== "unknown" && !text.includes(uiText.deviceTopology.notReportedMarker));
}

// 后端给的是状态 id（见 DeviceNetworkConnectionStates），措辞在这里。
function userFacingNetworkState(value?: string | null) {
  switch (String(value ?? "").trim()) {
    case "connected": return uiText.deviceTopology.connectionState.connected;
    case "disconnected": return uiText.deviceTopology.connectionState.disconnected;
    case "not-reported": return uiText.deviceTopology.notReportedMarker;
    default: return uiText.deviceTopology.connectionState.unknown;
  }
}

// 高级互联节点的「硬件类别」就是它的互联角色，措辞在角色码里。
function hardwareKindLabel(port: DeviceTopologyPort) {
  return port.advancedInterconnect
    ? renderBackendMessage(port.advancedInterconnect.role, portHardwareKind(port))
    : portHardwareKind(port);
}

function isDeviceRole(role?: DeviceTopologyNodeRole) {
  return role === "external-hub"
    || role === "attached-device"
    || role === "internal-controller"
    || role === "internal-device"
    || role === "internal-function";
}

function isInterfaceRole(role?: DeviceTopologyNodeRole) {
  return role === "host-interface"
    || role === "downstream-interface"
    || role === "internal-interface";
}

function interfaceProtocol(node: DeviceTopologyListNode, scope: DeviceTopologyScope) {
  if (scope === "internal") {
    switch (node.connectorKind) {
      case "pcie": return "PCI Express";
      case "usb-internal": return "USB";
      case "internal-display": return node.port.display
        ? displayConnectorTechnology(node.port.display)
        : uiText.deviceTopology.internalDisplay;
      case "audio": return "HD Audio";
      case "bluetooth": return "Bluetooth";
      case "acpi": return "ACPI";
      case "storage": return node.port.storage?.busType ?? node.port.protocol;
      default: return node.port.protocol;
    }
  }
  return node.port.usb?.supportedProtocols
    ?? node.port.display?.connectorTechnology
    ?? node.port.protocol;
}

function interfaceSummaryFields(node: DeviceTopologyListNode, childCount: number, scope: DeviceTopologyScope) {
  if (scope === "external") {
    return deviceTopologySummaryFields(node.port);
  }
  return [
    { label: uiText.deviceTopology.label.interfaceType, value: node.title },
    { label: uiText.deviceTopology.label.connectionState, value: connectionStateLabel(node.connectionState) },
    { label: uiText.deviceTopology.label.directChildren, value: uiText.deviceTopology.countSuffix(childCount) },
    { label: uiText.deviceTopology.label.protocol, value: interfaceProtocol(node, scope) }
  ];
}

function connectionStateLabel(state?: "connected" | "disconnected" | "unknown") {
  if (state === "connected") return uiText.deviceTopology.connectionState.connected;
  if (state === "disconnected") return uiText.deviceTopology.connectionState.disconnected;
  return uiText.deviceTopology.connectionState.unknown;
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

function formatUsbPort(usb: DeviceTopologyUsbConnection) {
  return `Hub ${usb.portNumber}`;
}

function formatUsbVidPid(usb: DeviceTopologyUsbConnection) {
  if (!usb.vendorId && !usb.productId) {
    return "--";
  }

  return `${usb.vendorId ?? "VID --"} / ${usb.productId ?? "PID --"}`;
}

function formatUsbCapability(usb: DeviceTopologyUsbConnection) {
  if (usb.operatingAtSuperSpeedPlusOrHigher) {
    return uiText.deviceTopology.usbCapability.currentSuperSpeedPlus;
  }

  if (usb.operatingAtSuperSpeedOrHigher) {
    return uiText.deviceTopology.usbCapability.currentSuperSpeed;
  }

  if (usb.superSpeedPlusCapableOrHigher) {
    return uiText.deviceTopology.usbCapability.supportsSuperSpeedPlus;
  }

  if (usb.superSpeedCapableOrHigher) {
    return uiText.deviceTopology.usbCapability.supportsSuperSpeed;
  }

  if (usb.superSpeedCapableOrHigher === false || usb.superSpeedPlusCapableOrHigher === false) {
    return uiText.deviceTopology.usbCapability.notReported;
  }

  return "--";
}

function formatUsbConnector(usb: DeviceTopologyUsbConnection) {
  const connector = usb.portConnectorIsTypeC ? "USB-C" : "USB";
  if (usb.portIsUserConnectable === true) {
    return uiText.deviceTopology.connectorSuffix.userPluggable(connector);
  }

  if (usb.portIsUserConnectable === false) {
    return uiText.deviceTopology.connectorSuffix.internal(connector);
  }

  return usb.portConnectorIsTypeC ? connector : "--";
}

function connectorLabel(kind: string) {
  switch (kind) {
    case "usb-a":
      return uiText.deviceTopology.usbGenericConnector;
    case "usb-c":
      return "USB-C / Type-C";
    case "thunderbolt":
      return "Thunderbolt / USB4";
    case "hdmi":
      return "HDMI";
    case "displayport":
      return "DisplayPort";
    case "mini-displayport":
      return "Mini DisplayPort";
    case "dvi":
      return "DVI";
    case "vga":
      return "VGA / HD15";
    case "internal-display":
      return uiText.deviceTopology.deviceCategory.internalDisplayPanel;
    case "wireless-display":
      return uiText.deviceTopology.deviceCategory.wirelessDisplay;
    case "rj45":
      return "RJ45 / Ethernet";
    case "audio":
      return uiText.deviceTopology.deviceCategory.audio;
    case "sd-card":
      return uiText.deviceTopology.deviceCategory.cardReader;
    case "pcie":
      return "PCIe";
    case "bluetooth":
      return "Bluetooth";
    default:
      return uiText.deviceTopology.deviceCategory.generic;
  }
}

function busLabel(kind: string) {
  switch (kind) {
    case "usb":
      return "USB";
    case "usb4":
      return "USB4";
    case "thunderbolt":
      return "Thunderbolt";
    case "pci":
      return "PCI / PCIe";
    case "display":
      return uiText.deviceTopology.deviceCategory.display;
    case "network":
      return uiText.deviceTopology.deviceCategory.network;
    case "audio":
      return uiText.deviceTopology.deviceCategory.audio;
    case "storage":
      return uiText.deviceTopology.deviceCategory.storage;
    case "bluetooth":
      return uiText.deviceTopology.deviceCategory.bluetooth;
    case "system":
      return uiText.deviceTopology.deviceCategory.systemFirmware;
    default:
      return uiText.deviceTopology.deviceCategory.unknown;
  }
}
