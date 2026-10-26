import assert from "node:assert/strict";
import test from "node:test";
import { networkAdapter } from "../src/features/deviceTopology/adapters/internalDeviceAdapters.ts";
import { deviceUserDetailSections } from "../src/features/deviceTopology/deviceDetailSections.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { applyLanguage, languageOptions, uiText } from "../src/text.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";
import type { DeviceTopologyPort } from "../src/types.ts";

const languages = languageOptions.map(option => option.id)
  .filter((id): id is ConcreteAppLanguageMode => id !== "system");
const wireStates = ["connected", "disconnected", "unknown", "not-reported"] as const;

for (const language of languages) {
  test(`${language}: network adapters use product copy for every wire connection state`, async () => {
    const copy = await loadAppCopy(language);
    await applyLanguage(language);
    for (const state of wireStates) {
      const port: DeviceTopologyPort = {
        id: "network-adapter", isPhysicalConnector: false, displayName: "Fixture Ethernet",
        connectorKind: "internal", busKind: "network", hardwareKind: "Network Adapter", protocol: "Ethernet",
        pnpClass: "Net", deviceId: String.raw`PCI\VEN_1234&DEV_5678\NETWORK`, status: "OK",
        locationPaths: [], hardwareIds: [], compatibleIds: [],
        confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
        topologyPath: { domain: 5, code: 64, args: ["network-adapter"] },
        network: { interfaceName: "Fixture Ethernet", connectionState: state }
      };
      const model = networkAdapter.createModel({ scope: "internal", port, fallbackTitle: "Fixture Ethernet",
        downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 });
      const expected = state === "not-reported" ? copy.deviceTopology.notReportedMarker : copy.deviceTopology.connectionState[state];
      const diagnostic = `${language}: ${state}`;
      assert.ok(expected.trim(), diagnostic);
      assert.equal(model.connectionState, expected, diagnostic);
      assert.equal(model.summaryFields.find(field => field.label === uiText.deviceAdapters.label.connectionState)?.value, expected, diagnostic);
      assert.ok(model.subtitle.includes(expected), diagnostic);
      const sections = deviceUserDetailSections(port, { id: port.id, port, title: model.title, subtitle: model.subtitle,
        badge: model.badge, connectorKind: "internal", depth: 0, role: "internal-device",
        connectionState: state === "unknown" ? "connected" : "unknown" }, null, "internal", 0, []);
      const networkSection = sections.find(section => section.title === copy.deviceTopology.section.network);
      assert.equal(networkSection?.items.find(item => item.label === copy.deviceTopology.label.connectionState)?.value, expected, diagnostic);
    }
  });
}
