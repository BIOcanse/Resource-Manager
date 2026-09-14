import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  localSystemStatusDecoder,
  type LocalSystemStatus
} from "./localSystemStatusDecoder.ts";

type LocalSystemRequestClient = Pick<RequestClient, "request">;

export function getLocalSystemStatus(
  requestClient: LocalSystemRequestClient,
  signal?: AbortSignal
): Promise<LocalSystemStatus> {
  return requestClient.request({
    key: "local-system.status",
    url: "/api/local-system/status",
    fallbackError: "读取数据失败，请稍后重试",
    decoder: localSystemStatusDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildLocalSystemStatusSubscriptionUrl(
  intervalMs: number
): string {
  const params = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/local-system/status/subscribe?${params.toString()}`;
}

export type { LocalSystemStatus } from "./localSystemStatusDecoder.ts";
