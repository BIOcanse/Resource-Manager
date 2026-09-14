import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  resourceManagerSelfSchedulingDecoder,
  type ResourceManagerSelfSchedulingSnapshot
} from "./resourceManagerSelfSchedulingDecoder.ts";

type ResourceManagerSelfSchedulingRequestClient = Pick<RequestClient, "request">;

export function getResourceManagerSelfScheduling(
  requestClient: ResourceManagerSelfSchedulingRequestClient,
  signal?: AbortSignal
): Promise<ResourceManagerSelfSchedulingSnapshot> {
  return requestClient.request({
    key: "resource-manager.self-scheduling",
    url: "/api/adapters/resource-manager/scheduling",
    fallbackError: "读取资源管理器调度状态失败，请稍后重试",
    decoder: resourceManagerSelfSchedulingDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildResourceManagerSelfSchedulingSubscriptionUrl(
  intervalMs: number
): string {
  const params = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/adapters/resource-manager/scheduling/subscribe?${params.toString()}`;
}

export type {
  ResourceManagerSelfSchedulingSnapshot,
  ResourceManagerSelfSchedulingSource
} from "./resourceManagerSelfSchedulingDecoder.ts";
