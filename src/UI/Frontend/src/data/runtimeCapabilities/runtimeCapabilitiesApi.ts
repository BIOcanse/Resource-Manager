import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { runtimeCapabilitiesDecoder } from "./runtimeCapabilitiesDecoder.ts";
import type { BackendStartupCapabilities } from "../../types.ts";
import { uiText } from "../../text.ts";

type RuntimeCapabilitiesRequestClient = Pick<RequestClient, "request">;

export function getRuntimeCapabilities(
  requestClient: RuntimeCapabilitiesRequestClient,
  signal?: AbortSignal
): Promise<BackendStartupCapabilities> {
  return requestClient.request({
    key: "runtime.capabilities",
    url: "/api/runtime/capabilities",
    fallbackError: uiText.misc.runtimeCapabilityReadFailed,
    decoder: runtimeCapabilitiesDecoder,
    signal,
    request: { method: "GET" }
  });
}
