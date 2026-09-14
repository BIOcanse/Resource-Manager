import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  operationsStateDecoder,
  type HostManagerOperationsState
} from "./operationsStateDecoder.ts";

type OperationsRequestClient = Pick<RequestClient, "request">;

export function getOperationsState(
  requestClient: OperationsRequestClient,
  signal?: AbortSignal
): Promise<HostManagerOperationsState> {
  return requestClient.request({
    key: "host-manager.operations.state",
    url: "/api/operations",
    fallbackError: "读取后台操作状态失败，请稍后重试",
    decoder: operationsStateDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildOperationsSubscriptionUrl(): string {
  return "/api/operations/subscribe";
}

export type { HostManagerOperationsState } from "./operationsStateDecoder.ts";
