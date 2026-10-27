import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { appSettingsResultDecoder, type CommittedAppSettingsResult } from "../appSettings/appSettingsResultDecoder.ts";
import { memoryProtectionStatusDecoder, normalRunningPointDecoder, nullableNormalRunningPointDecoder } from "./memoryProtectionDecoders.ts";
import type { HostMemoryProtectionSettingsPatch, HostMemoryProtectionStatus } from "./memoryProtectionTypes.ts";
import { uiText } from "../../text.ts";

type Client = Pick<RequestClient, "request">;
export function getMemoryProtectionStatus(client: Client, signal?: AbortSignal) {
  return client.request({ key: "memory-protection.status.get", url: "/api/memory-protection",
    decoder: memoryProtectionStatusDecoder, fallbackError: uiText.misc.readSettingsFailed, signal, request: { method: "GET" } });
}
export function getNormalRunningPoint(client: Client, signal?: AbortSignal) {
  return client.request({ key: "memory-protection.normal-running-point.get", url: "/api/memory-protection/normal-running-point",
    decoder: nullableNormalRunningPointDecoder, fallbackError: uiText.misc.readSettingsFailed, signal, request: { method: "GET" } });
}
export function createNormalRunningPoint(client: Client, signal?: AbortSignal) {
  return client.request({ key: "memory-protection.normal-running-point.create", url: "/api/memory-protection/normal-running-point",
    decoder: normalRunningPointDecoder, fallbackError: uiText.stores.actionFailed, signal, request: { method: "POST" } });
}
export type MemoryProtectionReserveRebuildResult =
  | { status: 200; value: HostMemoryProtectionStatus }
  | { status: 409; value: HostMemoryProtectionStatus };
export async function rebuildMemoryProtectionReserve(client: Client, signal?: AbortSignal): Promise<MemoryProtectionReserveRebuildResult> {
  try {
    const value = await client.request({ key: "memory-protection.reserve.rebuild", url: "/api/memory-protection/reserve/rebuild",
      decoder: memoryProtectionStatusDecoder, fallbackError: uiText.stores.actionFailed, signal, request: { method: "POST" } });
    return { status: 200, value };
  } catch (error) {
    if (error instanceof RequestProblem && error.status === 409) {
      return { status: 409, value: memoryProtectionStatusDecoder.decode(error.payload) };
    }
    throw error;
  }
}
export type MemoryProtectionSettingsPatchResult =
  | { accepted: true; value: CommittedAppSettingsResult }
  | { accepted: false; status: 409; value: CommittedAppSettingsResult };
export async function patchMemoryProtectionSettings(client: Client, expectedRevision: string,
  settings: HostMemoryProtectionSettingsPatch, signal?: AbortSignal): Promise<MemoryProtectionSettingsPatchResult> {
  try {
    const value = await client.request({ key: "memory-protection.settings.patch", url: "/api/memory-protection",
      decoder: appSettingsResultDecoder, fallbackError: uiText.misc.saveSettingsFailed, signal,
      request: { method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedRevision, changes: { memoryProtection: settings } }) } });
    return { accepted: true, value };
  } catch (error) {
    if (error instanceof RequestProblem && error.status === 409) {
      return { accepted: false, status: 409, value: appSettingsResultDecoder.decode(error.payload) };
    }
    throw error;
  }
}
