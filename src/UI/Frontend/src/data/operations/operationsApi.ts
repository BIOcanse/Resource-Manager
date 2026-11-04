import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  operationsStateDecoder,
  type HostManagerOperationsState
} from "./operationsStateDecoder.ts";
import { uiText } from "../../text.ts";

type OperationsRequestClient = Pick<RequestClient, "request">;

export function getOperationsState(
  requestClient: OperationsRequestClient,
  signal?: AbortSignal
): Promise<HostManagerOperationsState> {
  return requestClient.request({
    key: "host-manager.operations.state",
    url: "/api/operations",
    fallbackError: uiText.misc.operationStateReadFailed,
    decoder: operationsStateDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildOperationsSubscriptionUrl(): string {
  return "/api/operations/subscribe";
}

export type { HostManagerOperationsState } from "./operationsStateDecoder.ts";
