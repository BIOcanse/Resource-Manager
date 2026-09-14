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
  const scopeTitle = () => props.scope === "external" ? "外部接口" : "内部接口";
  const scopeStatistics = () => props.scope === "external"
    ? `${externalTree().hostInterfaceCount} 本机 · ${externalTree().downstreamInterfaceCount} 扩展`
    : `${internalTree().interfaceCount} 接口 · ${internalTree().controllerCount} 控制器 · ${internalTree().attachedDeviceCount} 设备`;

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
        ariaLabel="设备管理分类"
        class="device-scope-tabs"
        itemClass="device-scope-tab"
        options={[
          { id: "external", label: "外部接口" },
          { id: "internal", label: "内部接口" }
        ]}
        onChange={(scope) => props.onScopeRequested?.(scope)}
      />
      <header class="device-topology-header">
        <div>
          <h2>设备拓扑</h2>
          <span>{snapshot()?.system.brandDisplayName ?? "读取中"}</span>
        </div>
      </header>

      <ObservationStateNotice
        state={topology.observation()}
        label="设备拓扑"
      />

      <Show
        when={snapshot()}
        fallback={
          <div class="device-topology-loading">
            {topology.state().state === "failed" ? "设备拓扑不可用" : "读取设备拓扑..."}
          </div>
        }
      >
        {(data) => (
            <div class="device-topology-layout">
              <aside class="device-port-list" aria-label={`${scopeTitle()}清单`}>
                <div class="device-port-list-title">
                  <strong>{scopeTitle()}</strong>
                  <span>
                    {!searchQuery().trim()
                      ? scopeStatistics()
                      : visibleNodes().length === sectionNodes().length
                        ? `${sectionNodes().length} 项`
                        : `${visibleNodes().length} / ${sectionNodes().length}`}
                  </span>
                </div>
                <input
                  class="device-port-search"
                  type="search"
                  value={searchQuery()}
                  onInput={(event) => setSearchQuery(event.currentTarget.value)}
                  placeholder={`搜索${scopeTitle()}`}
                  aria-label={`搜索${scopeTitle()}`}
                />
                <Show
                  when={visibleNodes().length > 0}
                  fallback={<p class="device-topology-empty">{sectionNodes().length > 0 ? "没有匹配项。" : `未识别到${scopeTitle()}。`}</p>}
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

              <div class="device-top-view" aria-label="电脑俯视图">
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
                  <span>{data().system.baseBoardManufacturer ?? "主板厂商未知"}</span>
                  <span>{data().system.baseBoardProduct ?? "主板型号未知"}</span>
                  <span>{data().system.biosVersion ?? "BIOS 未知"}</span>
                </div>
              </div>

              <aside class="device-port-detail" aria-label="拓扑详情">
                <Show
                  when={selectedPort()}
                  fallback={<p class="device-topology-empty">选择一个项目查看详情。</p>}
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
                        <div class="device-relation-path" aria-label="连接关系">
                          <For each={selectedNode()?.path ?? []}>
                            {(segment) => <span>{segment}</span>}
                          </For>
                        </div>
                      </Show>
                      <Show
                        when={selectedNode()?.specializedDevice}
                        fallback={
                          <div class="device-detail-summary" aria-label="关键信息">
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
                        详细信息
                      </button>
                      <Show when={false}>
                      <Show when={selectedNode()?.specializedDevice}>
                        {(model) => <DeviceSpecializedDetails model={model()} />}
                      </Show>
                      <div class="device-detail-divider" role="separator">
                        <span>全部详细信息</span>
                      </div>
                      <div class="device-detail-grid">
                        <DetailRow
                          label="分类"
                          value={selectedNodeIsDevice()
                            ? "连接设备"
                            : scopeTitle()}
                        />
                        <Show when={selectedNode()?.role}>
                          {(role) => <DetailRow label="节点角色" value={topologyRoleLabel(role())} />}
                        </Show>
                        <DetailRow
                          label={selectedNodeIsDevice() ? "设备类型" : "接口类型"}
                          value={selectedNodeIsDevice()
                            ? selectedNode()?.specializedDevice?.deviceTypeLabel ?? port().hardwareKind
                            : props.scope === "external"
                              ? connectorLabel(port().connectorKind)
                              : selectedNode()?.title ?? "内部接口"}
                        />
                        <DetailRow label="连接状态" value={connectionStateLabel(selectedNode()?.connectionState)} />
                        <Show when={selectedNodeIsDevice() && selectedParentNode()?.title}>
                          {(value) => <DetailRow label="上游节点" value={value()} />}
                        </Show>
                        <Show when={selectedNodeIsInterface()}>
                          <DetailRow label="直属节点" value={`${selectedNodeChildCount()} 个`} />
                        </Show>
                        <DetailRow label="总线" value={busLabel(port().busKind)} />
                        <Show when={!port().display}>
                          <DetailRow
                            label={selectedNodeIsDevice() ? "当前连接速率" : "当前接口速率"}
                            value={port().speed}
                          />
                        </Show>
                        <Show when={!port().display || port().physicalMaximumSpeed}>
                          <DetailRow label="物理最大速率" value={port().physicalMaximumSpeed ?? "--"} />
                        </Show>
                        <DetailRow label="协议" value={selectedNodeIsInterface() ? interfaceProtocol(selectedNode()!, props.scope) : port().protocol} />
                        <Show when={port().advancedInterconnect}>
                          {(interconnect) => (
                            <>
                              <DetailRow label="互连角色" value={interconnect().role} />
                              <DetailRow label="互连技术" value={interconnect().technology} />
                              <DetailRow label="角色证据" value={interconnect().evidence} />
                            </>
                          )}
                        </Show>
                        <Show when={!selectedNodeIsInterface()}>
                          <DetailRow label="PnP 类" value={port().pnpClass ?? "--"} />
                          <DetailRow label="厂商" value={port().manufacturer ?? "--"} />
                          <Show when={port().idResolution}>
                            {(identity) => (
                              <>
                                <DetailRow label="ID 数据库" value={`${identity().database} · ${identity().version}`} />
                                <Show when={distinctText(identity().vendorName, port().manufacturer)}>
                                  {(value) => <DetailRow label="数据库厂商" value={value()} />}
                                </Show>
                                <Show when={distinctText(identity().deviceName, port().displayName)}>
                                  {(value) => <DetailRow label="数据库设备" value={value()} />}
                                </Show>
                                <Show when={identity().subsystemName}>
                                  {(value) => <DetailRow label="数据库子系统" value={value()} />}
                                </Show>
                              </>
                            )}
                          </Show>
                          <DetailRow label="驱动服务" value={port().service ?? "--"} />
                          <DetailRow label="设备状态" value={port().status ?? "--"} />
                          <Show when={port().problemCode && port().problemCode !== 0}>
                            {(value) => <DetailRow label="设备问题码" value={String(value())} />}
                          </Show>
                        </Show>
                        <Show when={port().display}>
                          {(display) => (
                            <>
                              <Show when={selectedNodeIsDevice()}>
                                <DetailRow label="设备名称" value={display().monitorName} />
                                <Show when={display().resolution}>
                                  {(value) => <DetailRow label="活动分辨率" value={value()} />}
                                </Show>
                                <DetailRow label="活动刷新率" value={display().refreshRate} />
                              </Show>
                              <DetailRow label="连接位置" value={display().internal ? "机内连接" : "外部连接"} />
                              <Show when={display().connectorInstance > 0}>
                                <DetailRow label="连接器序号" value={String(display().connectorInstance)} />
                              </Show>
                            </>
                          )}
                        </Show>
                        <Show when={port().network}>
                          {(network) => (
                            <>
                              <DetailRow label="网络接口" value={network().interfaceName ?? "--"} />
                              <DetailRow label="连接状态" value={network().connectionState} />
                              <Show when={network().transmitLinkSpeed && network().receiveLinkSpeed && network().transmitLinkSpeed !== network().receiveLinkSpeed}>
                                <DetailRow label="发送速率" value={network().transmitLinkSpeed ?? "--"} />
                                <DetailRow label="接收速率" value={network().receiveLinkSpeed ?? "--"} />
                              </Show>
                              <Show when={network().activeMtuBytes}>
                                {(value) => <DetailRow label="活动 MTU" value={`${value()} B`} />}
                              </Show>
                              <Show when={network().permanentAddress}>
                                {(value) => <DetailRow label="MAC 地址" value={value()} />}
                              </Show>
                            </>
                          )}
                        </Show>
                        <Show when={port().usb}>
                          {(usb) => (
                            <>
                              <DetailRow label={selectedNodeIsDevice() ? "上游 Hub 端口" : "Hub 端口"} value={formatUsbPort(usb())} />
                              <Show when={props.scope === "internal"}>
                                <DetailRow label="USB 连接" value={usb().connectionStatus} />
                              </Show>
                              <Show when={!selectedNodeIsDevice()}>
                                <DetailRow label="连接器" value={formatUsbConnector(usb())} />
                                <DetailRow label="端口支持" value={usb().supportedProtocols} />
                                <DetailRow label="端口能力" value={formatUsbCapability(usb())} />
                                <Show when={usb().portIsDebugCapable === true}>
                                  <DetailRow label="USB 调试" value="支持" />
                                </Show>
                              </Show>
                              <Show when={selectedNodeIsDevice() && usb().deviceConnected}>
                                <DetailRow label="设备规范" value={usb().deviceSpecification} />
                                <DetailRow label="设备版本" value={usb().deviceRevision} />
                                <DetailRow label="USB 类" value={usb().deviceClass} />
                                <DetailRow label="USB VID/PID" value={formatUsbVidPid(usb())} />
                                <DetailRow label="设备地址" value={String(usb().deviceAddress)} />
                                <Show when={distinctText(usb().manufacturerName, port().manufacturer)}>
                                  {(value) => <DetailRow label="描述符厂商" value={value()} />}
                                </Show>
                                <Show when={distinctText(usb().productName, port().displayName)}>
                                  {(value) => <DetailRow label="描述符产品" value={value()} />}
                                </Show>
                              </Show>
                            </>
                          )}
                        </Show>
                        <DetailRow label="可信度" value={port().confidence} />
                        <DetailRow label="来源" value={port().source} />
                        <DetailRow label="上级设备" value={port().upstreamDisplayName ?? "--"} />
                        <DetailRow label="设备父级" value={port().nativeParentDisplayName ?? "--"} />
                        <DetailRow label="位置文本" value={port().locationInfo ?? "--"} />
                        <DetailRow label="类 GUID" value={port().classGuid ?? "--"} />
                      </div>
                      <Show when={!selectedNodeIsInterface() && (port().usb?.interfaceProtocols ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>当前配置接口</strong>
                          <For each={port().usb?.interfaceProtocols ?? []}>
                            {(protocol) => <code>{protocol}</code>}
                          </For>
                        </div>
                      </Show>
                      <Show when={!selectedNodeIsDevice() && (port().usb?.companionPorts ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>共享连接器 Companion</strong>
                          <For each={port().usb?.companionPorts ?? []}>
                            {(companion) => (
                              <code>
                                #{companion.companionIndex} · Hub 端口 {companion.portNumber}
                                {companion.hubSymbolicLinkName ? ` · ${companion.hubSymbolicLinkName}` : ""}
                              </code>
                            )}
                          </For>
                        </div>
                      </Show>
                      <Show when={selectedNodeIsDevice() && port().usb?.serialNumber}>
                        {(serialNumber) => (
                          <div class="device-id-block">
                            <strong>USB 序列号</strong>
                            <code>{serialNumber()}</code>
                          </div>
                        )}
                      </Show>
                      <div class="device-id-block">
                        <strong>关系链</strong>
                        <code>{port().topologyPath}</code>
                      </div>
                      <Show when={(port().locationPaths ?? []).length > 0}>
                        <div class="device-id-block">
                          <strong>位置路径</strong>
                          <For each={port().locationPaths}>
                            {(path) => <code>{path}</code>}
                          </For>
                        </div>
                      </Show>
                      <Show when={port().usb?.hubDevicePath}>
                        <div class="device-id-block">
                          <strong>Hub 设备路径</strong>
                          <code>{port().usb?.hubDevicePath}</code>
                        </div>
                      </Show>
                      <Show when={port().usb?.downstreamHubDevicePath}>
                        {(path) => (
                          <div class="device-id-block">
                            <strong>下游 Hub 路径</strong>
                            <code>{path()}</code>
                          </div>
                        )}
                      </Show>
                      <Show when={selectedNodeIsDevice() && port().display?.monitorDevicePath}>
                        {(devicePath) => (
                          <div class="device-id-block">
                            <strong>显示器设备路径</strong>
                            <code>{devicePath()}</code>
                          </div>
                        )}
                      </Show>
                      <Show when={selectedNodeIsDevice()}>
                        <div class="device-id-block">
                          <strong>{selectedNodeIsDevice() ? "设备标识" : "硬件 ID"}</strong>
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
                  <span>部分设备信息暂时不可用，可以稍后刷新。</span>
                </div>
              </Show>
            </div>
        )}
      </Show>
      <UserDetailsDialog
        open={detailsOpen() && Boolean(selectedPort())}
        title={`${selectedNode()?.title ?? "设备"}详细信息`}
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
    userDetailSection("概览", [
      ...summary
        .filter((field) => isReportedDeviceValue(field.value))
        .map((field) => userDetailItem(field.label, field.value)),
      userDetailItem("分类", isDevice ? "连接设备" : scope === "external" ? "外部接口" : "内部接口"),
      userDetailItem(isDevice ? "设备类型" : "接口类型", isDevice
        ? node.specializedDevice?.deviceTypeLabel ?? port.hardwareKind
        : connectorLabel(port.connectorKind)),
      userDetailItem("连接状态", connectionStateLabel(node.connectionState)),
      parent ? userDetailItem("连接到", parent.title) : null,
      isInterface ? userDetailItem("直属项目", `${childCount} 个`) : null
    ]),
    userDetailSection("连接", [
      userDetailItem("总线", reportedDeviceValue(busLabel(port.busKind))),
      userDetailItem("当前速率", reportedDeviceValue(port.speed)),
      userDetailItem("最高速率", reportedDeviceValue(port.physicalMaximumSpeed)),
      userDetailItem("协议", reportedDeviceValue(isInterface ? interfaceProtocol(node, scope) : port.protocol)),
      userDetailItem("厂商", reportedDeviceValue(port.manufacturer)),
      port.advancedInterconnect
        ? userDetailItem("互连技术", reportedDeviceValue(port.advancedInterconnect.technology))
        : null
    ]),
    display ? userDetailSection("显示", [
      userDetailItem("设备名称", reportedDeviceValue(display.monitorName)),
      userDetailItem("活动分辨率", reportedDeviceValue(display.resolution)),
      userDetailItem("刷新率", reportedDeviceValue(display.refreshRate)),
      userDetailItem("连接位置", display.internal ? "机内连接" : "外部连接")
    ]) : null,
    network ? userDetailSection("网络", [
      userDetailItem("网络接口", reportedDeviceValue(network.interfaceName)),
      userDetailItem("连接状态", userFacingNetworkState(network.connectionState)),
      userDetailItem("发送速率", reportedDeviceValue(network.transmitLinkSpeed)),
      userDetailItem("接收速率", reportedDeviceValue(network.receiveLinkSpeed)),
      network.activeMtuBytes ? userDetailItem("MTU", `${network.activeMtuBytes} B`) : null,
      userDetailItem("MAC 地址", reportedDeviceValue(network.permanentAddress))
    ]) : null,
    usb ? userDetailSection("USB", [
      userDetailItem("端口", formatUsbPort(usb)),
      userDetailItem("连接状态", userFacingUsbState(usb.connectionStatus)),
      userDetailItem("连接器", formatUsbConnector(usb)),
      userDetailItem("支持协议", reportedDeviceValue(usb.supportedProtocols)),
      userDetailItem("端口能力", reportedDeviceValue(formatUsbCapability(usb))),
      userDetailItem("设备规范", reportedDeviceValue(usb.deviceSpecification)),
      userDetailItem("设备版本", reportedDeviceValue(usb.deviceRevision)),
      userDetailItem("设备类型", reportedDeviceValue(usb.deviceClass)),
      userDetailItem("VID / PID", reportedDeviceValue(formatUsbVidPid(usb))),
      userDetailItem("序列号", reportedDeviceValue(usb.serialNumber))
    ]) : null,
    isDevice ? userDetailSection("设备标识", [
      userDetailItem("硬件标识", reportedDeviceValue(port.deviceId))
    ]) : null
  ]);
}

function reportedDeviceValue(value?: string | null) {
  return isReportedDeviceValue(value) ? String(value).trim() : "";
}

function isReportedDeviceValue(value?: string | null) {
  const text = String(value ?? "").trim();
  return Boolean(text && text !== "--" && text.toLocaleLowerCase() !== "unknown" && !text.includes("未报告"));
}

function userFacingNetworkState(value?: string | null) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "up":
    case "connected": return "已连接";
    case "down":
    case "disconnected": return "未连接";
    default: return "状态未知";
  }
}

function userFacingUsbState(value?: string | null) {
  const state = String(value ?? "").trim().toLocaleLowerCase();
  if (state.includes("no") || state.includes("not") || state.includes("disconnect") || state.includes("empty") || state.includes("none")) return "未连接";
  if (state.includes("connected")) return "已连接";
  return "状态未知";
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
      case "internal-display": return node.port.display?.connectorTechnology ?? "内置显示";
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
    { label: "接口类型", value: node.title },
    { label: "连接状态", value: connectionStateLabel(node.connectionState) },
    { label: "直属节点", value: `${childCount} 个` },
    { label: "协议", value: interfaceProtocol(node, scope) }
  ];
}

function connectionStateLabel(state?: "connected" | "disconnected" | "unknown") {
  if (state === "connected") return "已连接";
  if (state === "disconnected") return "未连接";
  return "状态未知";
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
    return "当前 SuperSpeedPlus 或更高";
  }

  if (usb.operatingAtSuperSpeedOrHigher) {
    return "当前 SuperSpeed 或更高";
  }

  if (usb.superSpeedPlusCapableOrHigher) {
    return "支持 SuperSpeedPlus 或更高";
  }

  if (usb.superSpeedCapableOrHigher) {
    return "支持 SuperSpeed 或更高";
  }

  if (usb.superSpeedCapableOrHigher === false || usb.superSpeedPlusCapableOrHigher === false) {
    return "未报告 SuperSpeed 能力";
  }

  return "--";
}

function formatUsbConnector(usb: DeviceTopologyUsbConnection) {
  const connector = usb.portConnectorIsTypeC ? "USB-C" : "USB";
  if (usb.portIsUserConnectable === true) {
    return `${connector} · 用户可插拔`;
  }

  if (usb.portIsUserConnectable === false) {
    return `${connector} · 内部连接`;
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
      return "USB-A / USB 连接";
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
      return "内置显示面板";
    case "wireless-display":
      return "无线 / 虚拟显示";
    case "rj45":
      return "RJ45 / Ethernet";
    case "audio":
      return "音频";
    case "sd-card":
      return "SD / 读卡器";
    case "pcie":
      return "PCIe";
    case "bluetooth":
      return "Bluetooth";
    default:
      return "通用设备";
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
      return "显示";
    case "network":
      return "网络";
    case "audio":
      return "音频";
    case "storage":
      return "存储";
    case "bluetooth":
      return "蓝牙";
    case "system":
      return "系统 / 固件";
    default:
      return "未知";
  }
}
