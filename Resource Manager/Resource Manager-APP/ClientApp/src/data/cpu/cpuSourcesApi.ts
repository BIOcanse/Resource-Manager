import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import type {
  CpuCoreResidencySnapshot,
  CpuExclusiveBindingSnapshot,
  CpuTopologySnapshot
} from "../../types.ts";
import {
  cpuExclusiveBindingsDecoder,
  cpuResidencyDecoder,
  cpuTopologyDecoder
} from "./cpuSourceDecoders.ts";

type CpuRequestClient = Pick<RequestClient, "request">;

export function buildCpuTopologySubscriptionUrl(intervalMs: number): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/cpu/topology/subscribe?${query}`;
}

export function buildCpuResidencySubscriptionUrl(intervalMs: number): string {
  const query = new URLSearchParams({ intervalMs: String(intervalMs) });
  return `/api/cpu/residency/subscribe?${query}`;
}

export function getCpuTopologySource(
  requestClient: CpuRequestClient,
  signal?: AbortSignal
): Promise<CpuTopologySnapshot | null> {
  return requestClient.request({
    key: "cpu.topology",
    url: "/api/cpu/topology",
    fallbackError: "CPU 拓扑读取失败",
    decoder: cpuTopologyDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getCpuResidencySource(
  requestClient: CpuRequestClient,
  signal?: AbortSignal
): Promise<CpuCoreResidencySnapshot | null> {
  return requestClient.request({
    key: "cpu.residency",
    url: "/api/cpu/residency",
    fallbackError: "CPU 驻留状态读取失败",
    decoder: cpuResidencyDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getCpuExclusiveBindingsSource(
  requestClient: CpuRequestClient,
  signal?: AbortSignal
): Promise<CpuExclusiveBindingSnapshot | null> {
  return requestClient.request({
    key: "cpu.exclusive-bindings",
    url: "/api/cpu/topology/exclusive-bindings",
    fallbackError: "CPU 独占绑定读取失败",
    decoder: cpuExclusiveBindingsDecoder,
    signal,
    request: { method: "GET" }
  });
}
