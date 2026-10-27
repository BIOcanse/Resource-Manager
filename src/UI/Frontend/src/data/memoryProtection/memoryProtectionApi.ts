import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { memoryProtectionStatusDecoder, normalRunningPointDecoder } from "./memoryProtectionDecoders.ts";
import type { HostMemoryProtectionStatus } from "./memoryProtectionTypes.ts";
import { uiText } from "../../text.ts";

type Client = Pick<RequestClient, "request">;
/** One subscription carries status, record and normal running point frames; see memoryProtectionSubscriptionSource. */
export function buildMemoryProtectionSubscriptionUrl(): string {
  return "/api/memory-protection/subscribe";
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
