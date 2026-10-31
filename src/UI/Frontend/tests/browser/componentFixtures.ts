// These are backend facts. Names, notes, state captions and capability labels
// must come from the frontend bundle used by the journey.
import type { ManagedComponent, ManagedComponentDefinition, ComponentVersionOptions } from "../../src/types.ts";

export type ComponentFixtureOverrides = Partial<Omit<ManagedComponent, "definition">> & { definition?: Partial<ManagedComponentDefinition> };
export function componentFixture(id = "hardware-bridge", overrides: ComponentFixtureOverrides = {}) {
  const runtime = id === "shared-webview2-runtime";
  const sensor = id === "hardware-sensors-pawnio-provider";
  const providerKind = runtime ? "runtime" : sensor ? "hardware_monitor" : "cpu";
  const vendor = runtime ? "Microsoft" : sensor ? "namazso" : "Resource Manager";
  const capabilities = (runtime ? ["webview2-runtime"] : sensor ? ["fan", "temperature", "voltage"] : ["power", "voltage"])
    .map(capability => ({ id: `${id}.${capability}`, providerId: id, providerKind }));
  const message = { domain: 1, code: 15, args: [] };
  return {
    state: "missing",
    installRoot: `C:/Fixture/Dependencies/${id}`,
    installerDirectory: "C:/Fixture/Dependencies/Installers",
    installerPath: null,
    installerAvailable: false,
    installed: false,
    providerActive: false,
    canDownload: true,
    canInstall: true,
    canVerify: true,
    installerSourceKind: runtime ? "direct" : "githubRelease",
    message,
    providers: [{ providerId: id, vendor, providerKind, state: "missing", message,
      runtimePath: null, capabilities: capabilities.map(capability => capability.id) }],
    ...overrides,
    definition: {
      id, vendor,
      sourcePageUrl: runtime ? "https://developer.microsoft.com/en-us/microsoft-edge/webview2/"
        : sensor ? "https://pawnio.eu/" : "https://github.com/BIOcanse/Resource-Manager-HardwareBridge",
      externalTermsUrl: runtime ? "https://www.microsoft.com/en-us/legal/terms-of-use" : sensor ? "https://pawnio.eu/" : null,
      managementRole: "dependency",
      isBundled: false,
      requiresElevation: runtime || sensor,
      requiresExternalTermsAcknowledgement: runtime || sensor,
      capabilities,
      ...overrides.definition
    }
  };
}

export function componentVersionFixture(id = "hardware-bridge"): ComponentVersionOptions {
  return {
    id, sourceKind: "githubRelease", catalogStatus: "partial", complete: false,
    options: [
      { choice: "verified", available: true, version: "v0.2.0", assetName: "ResourceManager.HardwareBridge-win-x64.zip", unavailableReasonCode: null },
      { choice: "latest", available: false, version: null, assetName: null, unavailableReasonCode: "network" },
      { choice: "latestStable", available: false, version: null, assetName: null, unavailableReasonCode: "no-stable-release" }
    ]
  };
}
