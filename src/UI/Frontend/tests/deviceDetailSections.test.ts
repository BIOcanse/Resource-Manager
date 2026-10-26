import assert from "node:assert/strict";
import { buildExternalInterfaceTree } from "../src/features/deviceTopology/externalInterfaceTree.ts";
import { portProtocol } from "../src/features/deviceTopology/deviceVocabulary.ts";
import {
  attachedDisplay,
  deviceUserDetailSections,
  nodeHeadline,
  nodeSummaryFields,
  type DeviceTopologyListNode
} from "../src/features/deviceTopology/deviceDetailSections.ts";
import type { DeviceTopologyDisplayConnection, DeviceTopologyPort, DeviceTopologyUsbConnection } from "../src/types.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";

/*
 * 设备拓扑「详细信息」弹窗的分节内容（2026-10-22 实机复核的问题）：
 * 同一事实不重复列出、集线器计数带量词、USB 端口号的说法、以太网协议本地化、
 * 端口上没接显示器时不列显示与 EDID 节。
 */

const rootHub = String.raw`\\?\usb#root_hub30#root`;
const hubPath = String.raw`\\?\usb#vid_1a40&pid_0101#hub`;

const keyboardHost = usbPort({
  id: "kbd-host", connectorKind: "usb-a", hubPath: rootHub, portNumber: 1, connected: true,
  productName: "HERO 99 HE", speed: "USB Full-Speed / 12Mbps",
  interfaces: [{ interfaceNumber: 0, alternateSetting: 0, classCode: 0x03, subClassCode: 0x01, protocolCode: 0x01 }],
  hid: { hidType: "keyboard", hidSpecification: "HID 1.11", inputPollingIntervalMicroseconds: 1_000, theoreticalReportRateHz: 1_000,
    standardCapabilitySource: { domain: 5, code: 60, args: [] } }
});
const hubHost = usbPort({
  id: "hub-host", connectorKind: "usb-a", hubPath: rootHub, portNumber: 2, connected: true, isHub: true,
  downstreamHubPath: String.raw`USB#VID_1A40&PID_0101#hub`, productName: "USB2.1 Hub", speed: "USB 2.0 High-Speed / 480Mbps"
});
const audioPort = usbPort({
  id: "audio", connectorKind: "usb-a", hubPath, portNumber: 2, connected: true,
  productName: "AB USB Audio", speed: "USB Full-Speed / 12Mbps",
  interfaces: [
    { interfaceNumber: 0, alternateSetting: 0, classCode: 0x01, subClassCode: 0x01, protocolCode: 0x00 },
    { interfaceNumber: 1, alternateSetting: 0, classCode: 0x01, subClassCode: 0x02, protocolCode: 0x00 },
    { interfaceNumber: 2, alternateSetting: 0, classCode: 0x03, subClassCode: 0x00, protocolCode: 0x00 }
  ]
});
const rj45: DeviceTopologyPort = {
  ...basePort("rj45", "rj45", "network"),
  displayName: "Fixture Gigabit Ethernet Adapter",
  protocol: "Ethernet",
  manufacturer: "Fixture",
  network: { interfaceName: "Ethernet 2", connectionState: "disconnected", permanentAddress: "000000000001", activeMtuBytes: 1500, hardwareInterface: true, connectorPresent: true, wireless: null }
};
const emptyHdmi: DeviceTopologyPort = {
  ...basePort("hdmi", "hdmi", "display"),
  protocol: "HDMI",
  physicalMaximumSpeed: "48 Gbps",
  display: display({ connectorTechnology: "HDMI 2.1", monitorName: "", active: false, targetAvailable: false })
};

const tree = buildExternalInterfaceTree([keyboardHost, hubHost, audioPort, rj45, emptyHdmi]);
const nodeTitled = (title: string, role?: string) => {
  const node = tree.nodes.find((candidate) => candidate.title === title && (!role || candidate.role === role));
  assert.ok(node, `${title} is in the tree`);
  return node as DeviceTopologyListNode;
};
const sections = (node: DeviceTopologyListNode) => {
  const parent = node.parentId ? tree.nodes.find((candidate) => candidate.id === node.parentId) as DeviceTopologyListNode ?? null : null;
  const childCount = tree.nodes.filter((candidate) => candidate.parentId === node.id).length;
  return deviceUserDetailSections(node.port, node, parent, "external", childCount, nodeSummaryFields(node, childCount, "external"));
};
const rows = (node: DeviceTopologyListNode) => sections(node).flatMap((section) => section.items.map((item) => `${section.title} | ${item.label} | ${item.value}`));
const values = (node: DeviceTopologyListNode) => sections(node).flatMap((section) => section.items.map((item) => item.value));
const section = (node: DeviceTopologyListNode, title: string) => sections(node).find((candidate) => candidate.title === title);
const t = uiText.deviceTopology;

// 键盘：「输入协议 键盘」已经写过，「设备类型 键盘」不再重复；连接到照常列出。
const keyboard = nodeTitled("HERO 99 HE", "attached-device");
assert.equal(values(keyboard).filter((value) => value === uiText.deviceAdapters.keyboard).length, 1,
  `the keyboard's type is shown once: ${rows(keyboard).join("\n")}`);
assert.ok(section(keyboard, t.section.overview)?.items.some((item) => item.label === t.label.connectedTo),
  "the keyboard keeps its connected-to row, nothing else names its upstream interface");

// 音频设备：「上游接口」与「连接到」、「当前链路」与「当前连接速率」同值，各留一行。
const audio = nodeTitled("AB USB Audio");
const audioRows = rows(audio);
assert.equal(values(audio).filter((value) => value === "USB-A 接口 2").length, 1, `the upstream interface is shown once: ${audioRows.join("\n")}`);
assert.equal(values(audio).filter((value) => value === "USB Full-Speed / 12Mbps").length, 1, `the current link is shown once: ${audioRows.join("\n")}`);
assert.ok(audioRows.includes(`${t.section.overview} | ${uiText.deviceAdapters.label.upstreamInterface} | USB-A 接口 2`));
assert.ok(audioRows.includes(`${t.section.overview} | ${uiText.deviceAdapters.label.currentLink} | USB Full-Speed / 12Mbps`));

// 集线器：计数带量词；USB 端口号说明是上级集线器上的第几个口，不像「第几个集线器」。
const hub = nodeTitled("USB2.1 Hub");
const hubSummary = nodeSummaryFields(hub, 1, "external");
assert.equal(hubSummary.find((field) => field.label === uiText.deviceAdapters.label.downstreamInterfaces)?.value, "1 个");
assert.equal(hubSummary.find((field) => field.label === uiText.deviceAdapters.label.connectedDevices)?.value, "1 个");
assert.equal(section(hub, "USB")?.items.find((item) => item.label === t.label.port)?.value, "上级集线器端口 2");
assert.ok(!values(hub).some((value) => /^Hub \d/.test(value)), "no raw 'Hub N' port text");

// RJ45：协议本地化；网络节不重复概览已有的「连接状态 未连接」。
const ethernet = nodeTitled(uiText.deviceInterface.connector.rj45);
const ethernetRows = rows(ethernet);
assert.ok(ethernetRows.includes(`${t.section.overview} | ${t.label.protocol} | 以太网`), ethernetRows.join("\n"));
assert.ok(!values(ethernet).includes("Ethernet"), "the protocol word is not left in English");
assert.equal(ethernetRows.filter((row) => row.endsWith(`| ${t.label.connectionState} | ${t.connectionState.disconnected}`)).length, 1,
  `the connection state is shown once: ${ethernetRows.join("\n")}`);
assert.ok(section(ethernet, t.section.network)?.items.some((item) => item.label === t.label.networkInterface), "the network section stays");

// 详情头的概括：接口写外部／内部接口，设备写设备类型，连接状态明确时跟在后面；不带速率和清单副标题（协调者 10-26）。
assert.equal(nodeHeadline(ethernet, "external"), `${t.externalScope} · ${t.connectionState.disconnected}`);
assert.equal(nodeHeadline(keyboard, "external"), `${uiText.deviceAdapters.keyboard} · ${t.connectionState.connected}`);
assert.ok(!nodeHeadline(audio, "external").includes("12Mbps") && nodeHeadline(audio, "external") !== audio.subtitle, "the headline carries no speed");
assert.equal(nodeHeadline({ ...ethernet, connectionState: "unknown" }, "internal"), t.internalScope, "an unknown state is left out");
// 没接显示器的 HDMI：不列显示节，EDID 详情也不渲染（attachedDisplay 为 null）。
const hdmi = nodeTitled(uiText.deviceInterface.connector.hdmi);
assert.equal(attachedDisplay(hdmi.port), null, "an empty port has no attached display");
assert.equal(section(hdmi, t.section.display), undefined, `no display section for an empty port: ${rows(hdmi).join("\n")}`);
assert.ok(section(hdmi, t.section.connection), "the port's own connection facts stay");
// 显示器接着但未激活：仍算接着，EDID 声明照常显示。
const inactive = { ...emptyHdmi, display: display({ connectorTechnology: "HDMI 2.1", monitorName: "Fixture Monitor", active: false, targetAvailable: false, edidRawDeclaredBase64: "AP///////wA=" }) };
assert.equal(attachedDisplay(inactive), inactive.display, "an inactive monitor is still attached");
assert.equal(attachedDisplay({ ...emptyHdmi, display: display({ connectorTechnology: null, monitorName: "", active: false, targetAvailable: true }) }) !== null, true,
  "a target Windows reports as available counts as attached");
assert.equal(attachedDisplay({ ...emptyHdmi, display: null }), null);

// 协议：普通英文词按当前语言出；内屏的协议是 Windows 输出技术 id 时按输出技术出；技术名原样。
assert.equal(portProtocol({ protocol: "Display", display: null }), t.deviceCategory.display);
assert.equal(portProtocol({ protocol: "Audio", display: null }), t.deviceCategory.audio);
assert.equal(portProtocol({ protocol: "displayport-embedded", display: display({ outputTechnology: "displayport-embedded", internal: true }) }),
  t.outputTechnology["displayport-embedded"], "an output technology id is not shown raw");
assert.equal(portProtocol({ protocol: "HID", display: null }), "HID");
assert.equal(portProtocol({ protocol: "Wi-Fi", display: null }), "Wi-Fi");

// 内部传输只读后端事实；设备名称、服务、连接器分类和父链不参与重判。
const pciPort: DeviceTopologyPort = {
  ...basePort("plain-pci", "pci", "pci"),
  transport: knownTransport("pci"),
  displayName: "USB4 Bluetooth xHCI controller", service: "stornvme", protocol: "HID",
  deviceId: "USB\\misleading", nativeParentDeviceId: "PCI\\parent", nativeParentDisplayName: "PCI Express root hub"
};
const internalNode: DeviceTopologyListNode = {
  id: pciPort.id, port: pciPort, title: uiText.deviceTree.pciInterface,
  subtitle: "", badge: "", connectorKind: "pcie", depth: 0, role: "internal-interface"
};
const internalSections = (node: DeviceTopologyListNode) => deviceUserDetailSections(node.port, node, null, "internal", 0, nodeSummaryFields(node, 0, "internal"));
const internalItems = (node: DeviceTopologyListNode) => internalSections(node).flatMap(section => section.items);
assert.equal(nodeSummaryFields(internalNode, 0, "internal").find(field => field.label === uiText.deviceAdapters.label.internalTransport)?.value, "PCI");
assert.ok(internalItems(internalNode).some(item => item.label === t.label.protocol && item.value === "HID"), "the independent protocol is preserved");
assert.ok(!internalItems(internalNode).some(item => /PCIe|PCI Express/.test(item.value)), "plain PCI has no fabricated Express label");
const unknownInternal: DeviceTopologyListNode = { ...internalNode, port: { ...pciPort,
  transport: { code: null, reason: "parent-chain-incomplete", ruleId: null, evidence: [] } } };
assert.equal(nodeSummaryFields(unknownInternal, 0, "internal").find(field => field.label === uiText.deviceAdapters.label.internalTransport)?.value,
  `${t.deviceCategory.unknown} (parent-chain-incomplete)`);
assert.ok(internalItems(unknownInternal).some(item => item.label === uiText.deviceAdapters.label.internalTransport
  && item.value === `${t.deviceCategory.unknown} (parent-chain-incomplete)`), "null transport reason remains visible in details");
const internalPanel: DeviceTopologyListNode = { ...unknownInternal, port: { ...unknownInternal.port,
  protocol: "displayport-embedded", display: display({ internal: true, active: true,
    connectorTechnology: "eDP 1.4", outputTechnology: "displayport-embedded", monitorName: "Fixture panel" }) } };
assert.ok(internalItems(internalPanel).some(item => item.label === uiText.deviceAdapters.label.displayTechnology && item.value === "eDP 1.4"),
  "display technology remains independent from unavailable transport");
const internalStorage: DeviceTopologyListNode = { ...unknownInternal, port: { ...unknownInternal.port,
  storage: { physicalDeviceId: "FIXTURE\\disk", busType: "SATA", partitions: [], source: { domain: 5, code: 35, args: [] } } } };
assert.ok(internalItems(internalStorage).some(item => item.label === uiText.deviceAdapters.label.busAndTransport && item.value === "SATA"),
  "reported storage bus remains separate from unavailable transport");

// 弹窗标题：名称和后缀之间有分隔，其他语言保持原有语序。
assert.equal(t.detailTitle("HERO 99 HE"), "详细信息：HERO 99 HE");
for (const [language, expected] of [["zh-TW", "詳細資訊：HERO 99 HE"], ["en-US", "HERO 99 HE details"], ["zh-CN", "详细信息：HERO 99 HE"]] as const) {
  applyLanguage(language);
  for (let attempt = 0; currentLanguage() !== language && attempt < 100; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(uiText.deviceTopology.detailTitle("HERO 99 HE"), expected);
  assert.ok(internalItems(unknownInternal).some(item => item.label === uiText.deviceAdapters.label.internalTransport
    && item.value === `${uiText.deviceTopology.deviceCategory.unknown} (parent-chain-incomplete)`), language);
  if (language === "en-US") {
    assert.ok(rows(ethernet).includes(`Overview | Protocol | Ethernet`), "English keeps Ethernet");
    assert.equal(section(hub, "USB")?.items.find((item) => item.label === "Port")?.value, "Parent hub port 2");
  }
}

console.log("deviceDetailSections: ok");

function basePort(id: string, connectorKind: string, busKind: string): DeviceTopologyPort {
  return {
    transport: { code: null, reason: "not-queried", ruleId: null, evidence: [] },
    id,
    isPhysicalConnector: true,
    displayName: null,
    connectorKind,
    busKind,
    hardwareKind: null,
    protocol: "",
    speed: null,
    deviceId: `FIXTURE\\${id}`,
    confidence: { domain: 5, code: 24, args: [] },
    source: { domain: 5, code: 35, args: [] },
    topologyPath: { domain: 5, code: 65, args: [] },
    locationPaths: [],
    hardwareIds: [],
    compatibleIds: []
  };
}

function knownTransport(code: NonNullable<DeviceTopologyPort["transport"]["code"]>): DeviceTopologyPort["transport"] {
  return { code, reason: null, ruleId: "fixture-authoritative-transport", evidence: [{
    source: "setupapi-cfgmgr32", deviceId: "FIXTURE\\transport", depth: 0,
    enumeratorName: null, busTypeGuid: null, service: null, parentDeviceId: null
  }] };
}

function display(overrides: Partial<DeviceTopologyDisplayConnection>): DeviceTopologyDisplayConnection {
  return {
    connectorTechnology: null,
    outputTechnology: "hdmi",
    monitorName: "",
    refreshRate: null,
    active: false,
    targetAvailable: false,
    internal: false,
    connectorInstance: 0,
    monitorDevicePath: null,
    edidRawDeclaredBase64: null,
    modeCapabilityDeclared: null,
    ...overrides
  };
}

function usbPort(options: {
  id: string;
  connectorKind: string;
  hubPath: string;
  portNumber: number;
  connected?: boolean;
  isHub?: boolean;
  downstreamHubPath?: string;
  productName?: string;
  speed?: string;
  interfaces?: DeviceTopologyUsbConnection["interfaces"];
  hid?: DeviceTopologyPort["hid"];
}): DeviceTopologyPort {
  const usb: DeviceTopologyUsbConnection = {
    hubDevicePath: options.hubPath,
    portNumber: options.portNumber,
    deviceConnected: options.connected === true,
    connectionStatus: options.connected ? { domain: 5, code: 47, args: [] } : { domain: 5, code: 46, args: [] },
    negotiatedSpeed: options.speed ?? null,
    deviceAddress: options.portNumber,
    deviceIsHub: options.isHub === true,
    supportedProtocols: "USB 1.1 / USB 2.0",
    portIsUserConnectable: true,
    companionPorts: [],
    deviceSpecification: "USB 2.0",
    deviceRevision: "1.0",
    deviceClass: options.isHub ? "Hub" : "Device",
    productName: options.productName,
    interfaceProtocols: [],
    deviceClassCode: options.isHub ? 0x09 : null,
    deviceSubClassCode: null,
    deviceProtocolCode: null,
    interfaces: options.interfaces ?? null,
    downstreamHubDevicePath: options.downstreamHubPath
  };
  return {
    ...basePort(options.id, options.connectorKind, "usb"),
    transport: { code: "usb", reason: null, ruleId: "fixture-usb-hub-ioctl", evidence: [{
      source: "usb-hub-ioctl", deviceId: `FIXTURE\\${options.id}`, depth: 0,
      enumeratorName: null, busTypeGuid: null, service: null, parentDeviceId: null
    }] },
    displayName: options.productName ?? null,
    hardwareKind: options.isHub ? "USB Hub" : "USB Device",
    protocol: "USB",
    speed: options.speed ?? null,
    physicalMaximumSpeed: "USB 2.0 High-Speed / 480Mbps",
    usb,
    hid: options.hid
  };
}
