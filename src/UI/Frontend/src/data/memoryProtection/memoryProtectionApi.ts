import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { defineResponseDecoder } from "../../frontendRuntime/request/ResponseDecoder.ts";
import {
  memoryProtectionStatusDecoder, normalRunningPointDecoder
} from "./memoryProtectionDecoders.ts";
import type { HostMemoryProtectionStatus, NormalRunningPoint } from "./memoryProtectionTypes.ts";
import { uiText } from "../../text.ts";

type Client = Pick<RequestClient, "request">;
/** One subscription carries status, record, point and saved points frames; see memoryProtectionSubscriptionSource. */
export function buildMemoryProtectionSubscriptionUrl(): string {
  return "/api/memory-protection/subscribe";
}
/**
 * A point change the backend refused, with its reason code as sent: normal-running-point-limit (409, the saved
 * points are at the limit) or normal-running-point-not-found (404, no saved point has that id).
 */
export type NormalRunningPointChange<T> = { status: "done"; value: T } | { status: "refused"; reason: string };
async function pointChange<T>(change: () => Promise<T>, refusedStatus: number): Promise<NormalRunningPointChange<T>> {
  try {
    return { status: "done", value: await change() };
  } catch (error) {
    const payload = error instanceof RequestProblem && error.status === refusedStatus ? error.payload : undefined;
    if (payload !== null && typeof payload === "object" && typeof (payload as { error?: unknown }).error === "string") {
      return { status: "refused", reason: (payload as { error: string }).error };
    }
    throw error;
  }
}
/** The new point becomes the one in effect; earlier points stay saved. */
export function createNormalRunningPoint(client: Client, signal?: AbortSignal): Promise<NormalRunningPointChange<NormalRunningPoint>> {
  return pointChange(() => client.request({ key: "memory-protection.normal-running-point.create",
    url: "/api/memory-protection/normal-running-point", decoder: normalRunningPointDecoder,
    fallbackError: uiText.stores.actionFailed, signal, request: { method: "POST" } }), 409);
}
export function selectNormalRunningPoint(client: Client, id: string): Promise<NormalRunningPointChange<NormalRunningPoint>> {
  return pointChange(() => client.request({ key: "memory-protection.normal-running-points.select",
    url: "/api/memory-protection/normal-running-points/active", decoder: normalRunningPointDecoder,
    fallbackError: uiText.stores.actionFailed,
    request: { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id }) } }), 404);
}
/** Deleting the point in effect leaves none in effect. */
export function deleteNormalRunningPoint(client: Client, id: string): Promise<NormalRunningPointChange<null>> {
  return pointChange(() => client.request({ key: "memory-protection.normal-running-points.delete",
    url: `/api/memory-protection/normal-running-points/${encodeURIComponent(id)}`,
    decoder: defineResponseDecoder<null>("memory-protection.empty.v1", () => null),
    fallbackError: uiText.stores.actionFailed, request: { method: "DELETE", allowEmptyResponse: true } }), 404);
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
