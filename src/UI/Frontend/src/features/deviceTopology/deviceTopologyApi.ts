import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
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

/*
 * 设备信息字段的当前值（后端 /api/device-topology/fields/subscribe）：每次采样完成推一帧完整的
 * informationFields，含 Sampled 字段（卷可用空间等）。结构订阅的帧只带 Fixed 与 EventDriven 字段，
 * 需要采样值的显示自己持有这个订阅。
 */
export function buildDeviceTopologyFieldsSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/device-topology/fields/subscribe?${query}`;
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
