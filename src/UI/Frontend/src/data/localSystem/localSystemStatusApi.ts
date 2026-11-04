import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  localSystemStatusDecoder,
  type LocalSystemStatus
} from "./localSystemStatusDecoder.ts";
import { uiText } from "../../text.ts";

type LocalSystemRequestClient = Pick<RequestClient, "request">;

export function getLocalSystemStatus(
  requestClient: LocalSystemRequestClient,
  signal?: AbortSignal
): Promise<LocalSystemStatus> {
  return requestClient.request({
    key: "local-system.status",
    url: "/api/local-system/status",
    fallbackError: uiText.misc.apiReadFailed,
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
