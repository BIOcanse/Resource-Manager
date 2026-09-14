import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { runtimeCapabilitiesDecoder } from "./runtimeCapabilitiesDecoder.ts";
import type { BackendStartupCapabilities } from "../../types.ts";

type RuntimeCapabilitiesRequestClient = Pick<RequestClient, "request">;

export function getRuntimeCapabilities(
  requestClient: RuntimeCapabilitiesRequestClient,
  signal?: AbortSignal
): Promise<BackendStartupCapabilities> {
  return requestClient.request({
    key: "runtime.capabilities",
    url: "/api/runtime/capabilities",
    fallbackError: "无法读取当前启动配置的运行能力",
    decoder: runtimeCapabilitiesDecoder,
    signal,
    request: { method: "GET" }
  });
}
