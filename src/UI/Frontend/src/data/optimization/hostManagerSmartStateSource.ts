import { decodeHostManagerRollbackState } from "../../api/hostManagerRollbackWire.ts";
import { defineResponseDecoder } from
  "../../frontendRuntime/request/ResponseDecoder.ts";

export const hostManagerSmartStateDecoder = defineResponseDecoder(
  "host-manager.smart-state.v2",
  decodeHostManagerRollbackState);

export function buildHostManagerSmartStateSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/optimization/smart/state/subscribe?${query.toString()}`;
}
