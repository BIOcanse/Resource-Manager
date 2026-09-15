import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
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
} from "../deviceTopology/deviceTopologyPresentation";
import {
  buildExternalInterfaceTree,
  externalInterfaceRoleLabel,
  filterExternalInterfaceTree,
  type ExternalInterfaceNodeRole
} from "../deviceTopology/externalInterfaceTree";
import {
  buildInternalInterfaceTree,
  filterInternalInterfaceTree,
  internalInterfaceRoleLabel,
  type InternalInterfaceNodeRole
} from "../deviceTopology/internalInterfaceTree";
import type { SpecializedDeviceModel } from "../deviceTopology/adapters/adapterRegistry";
import { useDeviceTopologyState } from "../deviceTopology/deviceTopologyStore";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import type { DeviceTopologyPort, DeviceTopologyUsbConnection } from "../types";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection
} from "../presentation/userDetails";
import { DeviceSpecializedDetails } from "./deviceTopology/DeviceSpecializedDetails";
import { DeviceSpecializedSummary } from "./deviceTopology/DeviceSpecializedSummary";
import { ObservationStateNotice } from "./ObservationStateNotice";
import { UserDetailsDialog } from "./UserDetailsDialog";
import { SegmentedControl } from "../ui/primitives/SegmentedControl";
import { renderBackendMessage } from "../presentation/backendMessage.ts";
import { uiText } from "../text.ts";

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
  const selectedNodeIsDevice = createMemo(() => isDeviceRole(selectedNode()?.role));
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
      <header class="device-topology-header">
        <div>
          <h2>{uiText.deviceTopology.panel}</h2>
          <span>{snapshot()?.system.brandDisplayName ?? uiText.deviceTopology.brandLoading}</span>
        </div>
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
                      <Show when={false}>
                      <Show when={selectedNode()?.specializedDevice}>
                        {(model) => <DeviceSpecializedDetails model={model()} />}
                      </Show>
                      <div class="device-detail-divider" role="separator">
                        <span>{uiText.deviceTopology.allDetails}</span>
                      </div>
                      <div class="device-detail-grid">
                        <DetailRow
                          label={uiText.deviceTopology.label.category}
                          value={selectedNodeIsDevice()
                            ? uiText.deviceTopology.connectedDevice
                            : scopeTitle()}
                        />
                        <Show when={selectedNode()?.role}>
                          {(role) => <DetailRow label={uiText.deviceTopology.label.nodeRole} value={topologyRoleLabel(role())} />}
                        </Show>
                        <DetailRow
                          label={selectedNodeIsDevice() ? uiText.deviceTopology.label.deviceType : uiText.deviceTopology.label.interfaceType}
                          value={selectedNodeIsDevice()
                            ? selectedNode()?.specializedDevice?.deviceTypeLabel ?? hardwareKindLabel(port())
                            : props.scope === "external"
                              ? connectorLabel(port().connectorKind)
                              : selectedNode()?.title ?? uiText.deviceTopology.internalScope}
                        />
                        <DetailRow label={uiText.deviceTopology.label.connectionState} value={connectionStateLabel(selectedNode()?.connectionState)} />
                        <Show when={selectedNodeIsDevice() && selectedParentNode()?.title}>
                          {(value) => <DetailRow label={uiText.deviceTopology.label.upstreamNode} value={value()} />}
                        </Show>
                        <Show when={selectedNodeIsInterface()}>
                          <DetailRow label={uiText.deviceTopology.label.directChildren} value={uiText.deviceTopology.countSuffix(selectedNodeChildCount())} />
                        </Show>
                        <DetailRow label={uiText.deviceTopology.label.bus} value={busLabel(port().busKind)} />
                        <Show when={!port().display}>
                          <DetailRow
                            label={selectedNodeIsDevice() ? uiText.deviceTopology.label.currentDeviceSpeed : uiText.deviceTopology.label.currentInterfaceSpeed}
                            value={port().speed}
                          />
                        </Show>
                        <Show when={!port().display || port().physicalMaximumSpeed}>
                          <DetailRow label={uiText.deviceTopology.label.physicalMaxSpeed} value={port().physicalMaximumSpeed ?? "--"} />
                        </Show>
                        <DetailRow label={uiText.deviceTopology.label.protocol} value={selectedNodeIsInterface() ? interfaceProtocol(selectedNode()!, props.scope) : port().protocol} />
                        <Show when={port().advancedInterconnect}>
                          {(interconnect) => (
                            <>
                              <DetailRow label={uiText.deviceTopology.label.interconnectRole} value={renderBackendMessage(interconnect().role)} />
                              <DetailRow label={uiText.deviceTopology.label.interconnectTechnology} value={interconnect().technology} />
                              <DetailRow label={uiText.deviceTopology.label.interconnectEvidence} value={renderBackendMessage(interconnect().evidence)} />
                            </>
                          )}
                        </Show>
                        <Show when={!selectedNodeIsInterface()}>
                          <DetailRow label={uiText.deviceTopology.label.pnpClass} value={port().pnpClass ?? "--"} />
                          <DetailRow label={uiText.deviceTopology.label.manufacturer} value={port().manufacturer ?? "--"} />
                          <Show when={port().idResolution}>
                            {(identity) => (
                              <>
                                <DetailRow label={uiText.deviceTopology.label.idDatabase} value={`${identity().database} · ${identity().version}`} />
                                <Show when={distinctText(identity().vendorName, port().manufacturer)}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.databaseVendor} value={value()} />}
                                </Show>
                                <Show when={distinctText(identity().deviceName, port().displayName)}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.databaseDevice} value={value()} />}
                                </Show>
                                <Show when={identity().subsystemName}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.databaseSubsystem} value={value()} />}
                                </Show>
                              </>
                            )}
                          </Show>
                          <DetailRow label={uiText.deviceTopology.label.driverService} value={port().service ?? "--"} />
                          <DetailRow label={uiText.deviceTopology.label.deviceStatus} value={port().status ?? "--"} />
                          <Show when={port().problemCode && port().problemCode !== 0}>
                            {(value) => <DetailRow label={uiText.deviceTopology.label.deviceProblemCode} value={String(value())} />}
                          </Show>
                        </Show>
                        <Show when={port().display}>
                          {(display) => (
                            <>
                              <Show when={selectedNodeIsDevice()}>
                                <DetailRow label={uiText.deviceTopology.label.deviceName} value={display().monitorName} />
                                <Show when={display().resolution}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.activeResolution} value={value()} />}
                                </Show>
                                <DetailRow label={uiText.deviceTopology.label.activeRefreshRate} value={display().refreshRate} />
                              </Show>
                              <DetailRow label={uiText.deviceTopology.label.connectionLocation} value={display().internal ? uiText.deviceTopology.displayLocation.internal : uiText.deviceTopology.displayLocation.external} />
                              <Show when={display().connectorInstance > 0}>
                                <DetailRow label={uiText.deviceTopology.label.connectorInstance} value={String(display().connectorInstance)} />
                              </Show>
                            </>
                          )}
                        </Show>
                        <Show when={port().network}>
                          {(network) => (
                            <>
                              <DetailRow label={uiText.deviceTopology.label.networkInterface} value={network().interfaceName ?? "--"} />
                              <DetailRow label={uiText.deviceTopology.label.connectionState} value={network().connectionState} />
                              <Show when={network().transmitLinkSpeed && network().receiveLinkSpeed && network().transmitLinkSpeed !== network().receiveLinkSpeed}>
                                <DetailRow label={uiText.deviceTopology.label.transmitSpeed} value={network().transmitLinkSpeed ?? "--"} />
                                <DetailRow label={uiText.deviceTopology.label.receiveSpeed} value={network().receiveLinkSpeed ?? "--"} />
                              </Show>
                              <Show when={network().activeMtuBytes}>
                                {(value) => <DetailRow label={uiText.deviceTopology.label.activeMtu} value={`${value()} B`} />}
                              </Show>
                              <Show when={network().permanentAddress}>
                                {(value) => <DetailRow label={uiText.deviceTopology.label.macAddress} value={value()} />}
                              </Show>
                            </>
                          )}
                        </Show>
                        <Show when={port().usb}>
                          {(usb) => (
                            <>
                              <DetailRow label={selectedNodeIsDevice() ? uiText.deviceTopology.label.upstreamHubPort : uiText.deviceTopology.label.hubPort} value={formatUsbPort(usb())} />
                              <Show when={props.scope === "internal"}>
                                <DetailRow label={uiText.deviceTopology.label.usbConnection} value={usb().connectionStatus} />
                              </Show>
                              <Show when={!selectedNodeIsDevice()}>
                                <DetailRow label={uiText.deviceTopology.label.connector} value={formatUsbConnector(usb())} />
                                <DetailRow label={uiText.deviceTopology.label.portProtocols} value={usb().supportedProtocols} />
                                <DetailRow label={uiText.deviceTopology.label.portCapability} value={formatUsbCapability(usb())} />
                                <Show when={usb().portIsDebugCapable === true}>
                                  <DetailRow label={uiText.deviceTopology.label.usbDebug} value={uiText.deviceTopology.label.supported} />
                                </Show>
                              </Show>
                              <Show when={selectedNodeIsDevice() && usb().deviceConnected}>
                                <DetailRow label={uiText.deviceTopology.label.deviceSpecification} value={usb().deviceSpecification} />
                                <DetailRow label={uiText.deviceTopology.label.deviceRevision} value={usb().deviceRevision} />
                                <DetailRow label={uiText.deviceTopology.label.usbClass} value={usb().deviceClass} />
                                <DetailRow label="USB VID/PID" value={formatUsbVidPid(usb())} />
                                <DetailRow label={uiText.deviceTopology.label.deviceAddress} value={String(usb().deviceAddress)} />
                                <Show when={distinctText(usb().manufacturerName, port().manufacturer)}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.descriptorVendor} value={value()} />}
                                </Show>
                                <Show when={distinctText(usb().productName, port().displayName)}>
                                  {(value) => <DetailRow label={uiText.deviceTopology.label.descriptorProduct} value={value()} />}
                                </Show>
                              </Show>
                            </>
                          )}
                        </Show>
                        <DetailRow label={uiText.deviceTopology.label.confidence} value={renderBackendMessage(port().confidence)} />
                        <DetailRow label={uiText.deviceTopology.label.source} value={renderBackendMessage(port().source)} />
                        <DetailRow label={uiText.deviceTopology.label.upstreamDevice} value={port().upstreamDisplayName ?? "--"} />
                        <DetailRow label={uiText.deviceTopology.label.deviceParent} value={port().nativeParentDisplayName ?? "--"} />
                        <DetailRow label={uiText.deviceTopology.label.locationText} value={port().locationInfo ?? "--"} />
                        <DetailRow label={uiText.deviceTopology.label.classGuid} value={port().classGuid ?? "--"} />
                      </div>
                      <Show when={!selectedNodeIsInterface() && (port().usb?.interfaceProtocols ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>{uiText.deviceTopology.group.activeConfigurationInterface}</strong>
                          <For each={port().usb?.interfaceProtocols ?? []}>
                            {(protocol) => <code>{protocol}</code>}
                          </For>
                        </div>
                      </Show>
                      <Show when={!selectedNodeIsDevice() && (port().usb?.companionPorts ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>{uiText.deviceTopology.group.sharedConnectorCompanion}</strong>
                          <For each={port().usb?.companionPorts ?? []}>
                            {(companion) => (
                              <code>
                                #{companion.companionIndex} · {uiText.deviceTopology.label.hubPort} {companion.portNumber}
                                {companion.hubSymbolicLinkName ? ` · ${companion.hubSymbolicLinkName}` : ""}
                              </code>
                            )}
                          </For>
                        </div>
                      </Show>
                      <Show when={selectedNodeIsDevice() && port().usb?.serialNumber}>
                        {(serialNumber) => (
                          <div class="device-id-block">
                            <strong>{uiText.deviceTopology.group.usbSerialNumber}</strong>
                            <code>{serialNumber()}</code>
                          </div>
                        )}
                      </Show>
                      <div class="device-id-block">
                        <strong>{uiText.deviceTopology.group.relationChain}</strong>
                        <code>{port().topologyPath}</code>
                      </div>
                      <Show when={(port().locationPaths ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>{uiText.deviceTopology.group.locationPath}</strong>
                          <For each={port().locationPaths}>
                            {(path) => <code>{path}</code>}
                          </For>
                        </div>
                      </Show>
                      <Show when={port().usb?.hubDevicePath}>
                        <div class="device-id-block">
                          <strong>{uiText.deviceTopology.group.hubDevicePath}</strong>
                          <code>{port().usb?.hubDevicePath}</code>
                        </div>
                      </Show>
                      <Show when={port().usb?.downstreamHubDevicePath}>
                        {(path) => (
                          <div class="device-id-block">
                            <strong>{uiText.deviceTopology.group.downstreamHubPath}</strong>
                            <code>{path()}</code>
                          </div>
                        )}
                      </Show>
                      <Show when={selectedNodeIsDevice() && port().display?.monitorDevicePath}>
                        {(devicePath) => (
                          <div class="device-id-block">
                            <strong>{uiText.deviceTopology.group.displayDevicePath}</strong>
                            <code>{devicePath()}</code>
                          </div>
                        )}
                      </Show>
                      <Show when={selectedNodeIsDevice()}>
                        <div class="device-id-block">
                          <strong>{selectedNodeIsDevice() ? uiText.deviceTopology.section.deviceIdentity : uiText.deviceTopology.label.hardwareId}</strong>
                          <code>{port().deviceId}</code>
                        </div>
                      </Show>
                      </Show>
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

  return compactUserDetailSections([
    userDetailSection(uiText.deviceTopology.section.overview, [
      ...summary
        .filter((field) => isReportedDeviceValue(field.value))
        .map((field) => userDetailItem(field.label, field.value)),
      userDetailItem(uiText.deviceTopology.label.category, isDevice ? uiText.deviceTopology.connectedDevice : scope === "external" ? uiText.deviceTopology.externalScope : uiText.deviceTopology.internalScope),
      userDetailItem(isDevice ? uiText.deviceTopology.label.deviceType : uiText.deviceTopology.label.interfaceType, isDevice
        ? node.specializedDevice?.deviceTypeLabel ?? hardwareKindLabel(port)
        : connectorLabel(port.connectorKind)),
      userDetailItem(uiText.deviceTopology.label.connectionState, connectionStateLabel(node.connectionState)),
      parent ? userDetailItem(uiText.deviceTopology.label.connectedTo, parent.title) : null,
      isInterface ? userDetailItem(uiText.deviceTopology.label.directItems, uiText.deviceTopology.countSuffix(childCount)) : null
    ]),
    userDetailSection(uiText.deviceTopology.section.connection, [
      userDetailItem(uiText.deviceTopology.label.bus, reportedDeviceValue(busLabel(port.busKind))),
      userDetailItem(uiText.deviceTopology.label.currentSpeed, reportedDeviceValue(port.speed)),
      userDetailItem(uiText.deviceTopology.label.maxSpeed, reportedDeviceValue(port.physicalMaximumSpeed)),
      userDetailItem(uiText.deviceTopology.label.protocol, reportedDeviceValue(isInterface ? interfaceProtocol(node, scope) : port.protocol)),
      userDetailItem(uiText.deviceTopology.label.manufacturer, reportedDeviceValue(port.manufacturer)),
      port.advancedInterconnect
        ? userDetailItem(uiText.deviceTopology.label.interconnectTechnology, reportedDeviceValue(port.advancedInterconnect.technology))
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
      userDetailItem(uiText.deviceTopology.label.connectionState, userFacingUsbState(usb.connectionStatus)),
      userDetailItem(uiText.deviceTopology.label.connector, formatUsbConnector(usb)),
      userDetailItem(uiText.deviceTopology.label.supportedProtocols, reportedDeviceValue(usb.supportedProtocols)),
      userDetailItem(uiText.deviceTopology.label.portCapability, reportedDeviceValue(formatUsbCapability(usb))),
      userDetailItem(uiText.deviceTopology.label.deviceSpecification, reportedDeviceValue(usb.deviceSpecification)),
      userDetailItem(uiText.deviceTopology.label.deviceRevision, reportedDeviceValue(usb.deviceRevision)),
      userDetailItem(uiText.deviceTopology.label.deviceType, reportedDeviceValue(usb.deviceClass)),
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

function userFacingNetworkState(value?: string | null) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "up":
    case "connected": return uiText.deviceTopology.connectionState.connected;
    case "down":
    case "disconnected": return uiText.deviceTopology.connectionState.disconnected;
    default: return uiText.deviceTopology.connectionState.unknown;
  }
}

function userFacingUsbState(value?: string | null) {
  const state = String(value ?? "").trim().toLocaleLowerCase();
  if (state.includes("no") || state.includes("not") || state.includes("disconnect") || state.includes("empty") || state.includes("none")) return uiText.deviceTopology.connectionState.disconnected;
  if (state.includes("connected")) return uiText.deviceTopology.connectionState.connected;
  return uiText.deviceTopology.connectionState.unknown;
}

// 高级互联节点的「硬件类别」就是它的互联角色，措辞在角色码里。
function hardwareKindLabel(port: DeviceTopologyPort) {
  return port.advancedInterconnect
    ? renderBackendMessage(port.advancedInterconnect.role, port.hardwareKind)
    : port.hardwareKind;
}

function DetailRow(props: { label: string; value: string }) {
  return (
    <>
      <span>{props.label}</span>
      <strong>{props.value}</strong>
    </>
  );
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

function topologyRoleLabel(role: DeviceTopologyNodeRole) {
  return role.startsWith("internal-")
    ? internalInterfaceRoleLabel(role as InternalInterfaceNodeRole)
    : externalInterfaceRoleLabel(role as ExternalInterfaceNodeRole);
}

function interfaceProtocol(node: DeviceTopologyListNode, scope: DeviceTopologyScope) {
  if (scope === "internal") {
    switch (node.connectorKind) {
      case "pcie": return "PCI Express";
      case "usb-internal": return "USB";
      case "internal-display": return node.port.display?.connectorTechnology ?? uiText.deviceTopology.internalDisplay;
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

function distinctText(value?: string | null, reference?: string | null) {
  const normalized = value?.trim();
  if (!normalized || normalized.localeCompare(reference?.trim() ?? "", undefined, { sensitivity: "accent" }) === 0) {
    return null;
  }

  return normalized;
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
