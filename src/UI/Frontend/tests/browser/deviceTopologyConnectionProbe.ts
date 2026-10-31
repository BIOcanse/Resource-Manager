import type { Page } from "playwright-core";
import { NOT_GHOST } from "./liveLocator.ts";
// Reuse the regular topology subscription and UI; these ports follow the production wire shape.
export function networkPort(state: "connected" | "disconnected" | "unknown" | "not-reported") {
  return {
    id: "fixture-network", isPhysicalConnector: false, displayName: "Fixture Ethernet",
    connectorKind: "internal", busKind: "network", hardwareKind: "Network Adapter", protocol: "Ethernet",
    speed: null, physicalMaximumSpeed: null,
    deviceId: String.raw`PCI\VEN_1234&DEV_5678\NETWORK`, pnpClass: "Net", manufacturer: "Frontend Harness",
    service: "fixture-network", status: "OK",
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    upstreamDeviceId: null, upstreamDisplayName: null,
    topologyPath: { domain: 5, code: 64, args: ["fixture-network"] },
    nativeParentDeviceId: null, nativeParentDisplayName: null, locationInfo: null, locationPaths: [], classGuid: null,
    display: null,
    network: { interfaceName: "Fixture Ethernet", connectionState: state,
      transmitLinkSpeed: null, receiveLinkSpeed: null, permanentAddress: null,
      activeMtuBytes: null, hardwareInterface: true, connectorPresent: true, wireless: null },
    transport: { code: null, reason: "not-queried", ruleId: null, evidence: [] },
    idResolution: null, advancedInterconnect: null, usb: null, hardwareIds: [], compatibleIds: [],
    devNodeStatus: null, problemCode: null, hid: null, camera: null, smartDevice: null, storage: null
  };
}

export async function checkNetworkConnectionStates(page: Page, app: Awaited<ReturnType<typeof import("../../src/i18n/copy/appCopyLoader.ts").loadAppCopy>>, language: string, baseUrl: string) {
  const topology = page.locator(".device-topology-panel");
  try {
    await topology.getByRole("radio", { name: app.deviceTopology.internalScope, exact: true }).click();
    for (const state of ["connected", "disconnected", "unknown", "not-reported"] as const) {
      const response = await fetch(`${baseUrl}/__test/scenario`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceTopologyPorts: [networkPort(state)] })
      });
      if (!response.ok) throw new Error(`${language}: network fixture rejected: ${response.status}`);
      const node = topology.locator(".device-port-item.device-node").filter({ hasText: "Fixture Ethernet" }).first();
      await node.click();
      const expected = state === "not-reported" ? app.deviceTopology.notReportedMarker : app.deviceTopology.connectionState[state];
      const summaryRow = topology.locator(".device-detail-summary-item")
        .filter({ has: page.getByText(app.deviceAdapters.label.connectionState, { exact: true }) });
      await summaryRow.locator("strong").getByText(expected, { exact: true }).waitFor();
      await topology.locator(".device-details-button").click();
      const dialog = page.locator(`.user-details-modal${NOT_GHOST}:visible`);
      const facts = dialog.locator(".device-specialized-details.kind-network-adapter");
      await facts.getByText(expected, { exact: true }).waitFor();
      const actual = await facts.locator("strong").allTextContents();
      if (!actual.includes(expected) || actual.includes(state))
        throw new Error(`${language}: ${state} leaked into network details: ${JSON.stringify(actual)}`);
      await page.keyboard.press("Escape");
      await dialog.waitFor({ state: "hidden" });
    }
  } finally {
    await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ deviceTopologyPorts: null }) });
    await topology.getByRole("radio", { name: app.deviceTopology.externalScope, exact: true }).click();
    await topology.locator(".device-port-item").first().click();
  }
}
