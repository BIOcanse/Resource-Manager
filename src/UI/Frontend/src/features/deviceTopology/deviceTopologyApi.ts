import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import type { SubscriptionFieldReference } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import type { DeviceTopologySnapshotState } from "../../types";
import { deviceTopologyStateDecoder } from "./deviceTopologyStateDecoder.ts";
import { uiText } from "../../text.ts";

type DeviceTopologyRequestClient = Pick<RequestClient, "request">;

export function buildDeviceTopologyStateSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/device-topology/state/subscribe?${query}`;
}

/* Subscribe separately to selected sampled fields by device address. Frames contain current selected values and later changes; structure frames include only fixed and event-driven fields. Empty selections are invalid. */
export function buildDeviceTopologyFieldsSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/device-topology/fields/subscribe?${query}`;
}

/** Sort and deduplicate device/field selections so equivalent sets share one query. */
export function normalizeInformationFieldSelection(
  fields: readonly SubscriptionFieldReference[]
): readonly SubscriptionFieldReference[] {
  const unique = new Map<string, SubscriptionFieldReference>();
  for (const field of fields) {
    unique.set(JSON.stringify([field.deviceIdentity, field.fieldName]),
      { deviceIdentity: field.deviceIdentity, fieldName: field.fieldName });
  }
  return [...unique.entries()].sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0).map(([, field]) => field);
}

export function getDeviceTopologyState(
  requestClient: DeviceTopologyRequestClient,
  signal?: AbortSignal
): Promise<DeviceTopologySnapshotState> {
  return requestClient.request({
    key: "device-topology.state",
    url: "/api/device-topology/state",
    fallbackError: uiText.deviceTopologyState.readFailedRetry,
    decoder: deviceTopologyStateDecoder,
    signal,
    request: { method: "GET" }
  });
}
