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
import { uiText } from "../../text.ts";

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
    fallbackError: uiText.misc.cpuTopologyReadFailed,
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
    fallbackError: uiText.misc.cpuResidencyReadFailed,
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
    fallbackError: uiText.misc.cpuExclusiveBindingReadFailed,
    decoder: cpuExclusiveBindingsDecoder,
    signal,
    request: { method: "GET" }
  });
}
