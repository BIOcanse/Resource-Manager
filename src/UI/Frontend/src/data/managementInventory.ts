import type { ManagedComponent, SoftwareRecord } from "../types.ts";
import type { BrowserRuntimeEntry, BrowserRuntimeSnapshot } from "../features/browserRuntimes/browserRuntimeTypes.ts";
import { decodeManagedComponents } from "../features/management/componentDecoder.ts";
import { decodeSoftwareRecords } from "../features/management/softwareRecordDecoder.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import type { SourceRefreshOptions } from "../frontendRuntime/source/SourceDescriptor.ts";
import { defineResponseDecoder, requireArray, requireRecord, requireString, requireNonEmptyString,
  requireBoolean, requireNullable, requireOneOf, ResponseDecodeError } from "../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../text.ts";

export const managementComponentsDecoder = defineResponseDecoder<ManagedComponent[]>("management.components.v1", decodeManagedComponents);
export const managementSoftwareDecoder = defineResponseDecoder<SoftwareRecord[]>("management.software.v1", decodeSoftwareRecords);
function decodeRuntime(value: unknown, path: string): BrowserRuntimeEntry {
  const row = requireRecord(value, path);
  return {
    id: requireNonEmptyString(row.id, path + ".id"),
    name: requireString(row.name, path + ".name"),
    kind: requireOneOf(row.kind, path + ".kind", ["web_view2_runtime", "chromium_browser", "gecko_browser"]),
    version: requireString(row.version, path + ".version"),
    executablePath: requireString(row.executablePath, path + ".executablePath"),
    runtimeDirectory: requireString(row.runtimeDirectory, path + ".runtimeDirectory"),
    source: requireOneOf(row.source, path + ".source", ["system_machine", "system_user", "managed", "browser_registration", "known_install"]),
    nativeWebView2Compatible: requireBoolean(row.nativeWebView2Compatible, path + ".nativeWebView2Compatible"),
    selected: requireBoolean(row.selected, path + ".selected")
  };
}
export const managementBrowserRuntimesDecoder = defineResponseDecoder<BrowserRuntimeSnapshot>("management.browser-runtimes.v1", value => {
  const row = requireRecord(value, "$");
  const capturedAt = requireString(row.capturedAt, "$.capturedAt");
  if (!Number.isFinite(Date.parse(capturedAt))) throw new ResponseDecodeError("$.capturedAt", "timestamp");
  return {
    sharedRuntime: requireNullable(row.sharedRuntime, "$.sharedRuntime", decodeRuntime),
    browserFallback: requireNullable(row.browserFallback, "$.browserFallback", decodeRuntime),
    candidates: requireArray(row.candidates, "$.candidates").map((item, index) => decodeRuntime(item, "$.candidates[" + index + "]")),
    installComponentId: requireNonEmptyString(row.installComponentId, "$.installComponentId"),
    capturedAt
  };
});
export function getManagementComponents(client: Pick<RequestClient, "request">, signal: AbortSignal) {
  return client.request({ key: "management.components", url: "/api/components", signal,
    decoder: managementComponentsDecoder, fallbackError: uiText.stores.componentCatalogRefreshFailed, request: { method: "GET" } });
}
export function getManagementSoftware(client: Pick<RequestClient, "request">, signal: AbortSignal, options: SourceRefreshOptions) {
  return client.request({ key: "management.software", url: "/api/software" + (options.force ? "?refresh=true" : ""), signal,
    decoder: managementSoftwareDecoder, fallbackError: uiText.stores.softwareRegistryRefreshFailed, request: { method: "GET" } });
}
export function getManagementBrowserRuntimes(client: Pick<RequestClient, "request">, signal: AbortSignal, options: SourceRefreshOptions) {
  return client.request({ key: "management.browser-runtimes", url: "/api/browser-runtimes" + (options.force ? "?refresh=true" : ""), signal,
    decoder: managementBrowserRuntimesDecoder, fallbackError: uiText.stores.browserRuntimeRefreshFailed, request: { method: "GET" } });
}
