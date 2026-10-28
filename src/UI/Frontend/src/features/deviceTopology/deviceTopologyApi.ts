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

/*
 * 设备信息字段的当前值（后端 /api/device-topology/fields/subscribe）。订阅项带字段选择（设备地址和字段名），
 * 后端只发选中的字段，订阅时发一帧当前值，之后只在值变化时发；帧与 informationFields 同形，只含选中的设备和字段。
 * 结构订阅的帧只带 Fixed 与 EventDriven 字段，需要采样值（卷可用空间、手机电量等）的显示自己持有这个订阅。
 * 选择不能为空（后端回 400）：没有要选的字段时不订阅。
 */
export function buildDeviceTopologyFieldsSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/device-topology/fields/subscribe?${query}`;
}

/** 字段选择按设备地址、字段名排序并去重：同一组字段只是一个查询，后端也要求每个字段只选一次。 */
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
