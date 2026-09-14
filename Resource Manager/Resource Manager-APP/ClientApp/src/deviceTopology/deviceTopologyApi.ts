import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import type { DeviceTopologySnapshotState } from "../types";
import { deviceTopologyStateDecoder } from "./deviceTopologyStateDecoder.ts";

type DeviceTopologyRequestClient = Pick<RequestClient, "request">;

export function buildDeviceTopologyStateSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/device-topology/state/subscribe?${query}`;
}

export function getDeviceTopologyState(
  requestClient: DeviceTopologyRequestClient,
  signal?: AbortSignal
): Promise<DeviceTopologySnapshotState> {
  return requestClient.request({
    key: "device-topology.state",
    url: "/api/device-topology/state",
    fallbackError: "设备拓扑读取失败，请稍后重试",
    decoder: deviceTopologyStateDecoder,
    signal,
    request: { method: "GET" }
  });
}
