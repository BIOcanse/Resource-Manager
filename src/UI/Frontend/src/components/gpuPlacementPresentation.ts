import type { GpuPlacementProviderCapability } from "../types.ts";
import { uiText } from "../text.ts";

export function gpuPlacementReason(code?: string | null): string {
  const reasons = uiText.softwareDetail.providerPhase.reasons;
  return reasons[code as keyof typeof reasons] ?? uiText.softwareDetail.providerPhase.unknown;
}

export function formatGpuProviderCapability(phase: string, capability: GpuPlacementProviderCapability): string {
  const copy = uiText.softwareDetail.providerPhase;
  const state = capability.state === "supported" ? copy.available
    : capability.state === "unsupported" ? copy.unsupported : copy.unknown;
  let reason = gpuPlacementReason(capability.reasonCode);
  if (capability.reasonCode === "startup-d3d-creation-only") {
    const entries: string[] = [];
    if (capability.graphicsApi.includes("D3D11")) entries.push("D3D11CreateDevice / D3D11CreateDeviceAndSwapChain");
    if (capability.graphicsApi.includes("D3D12")) entries.push(
      `D3D12CreateDevice / ID3D12DeviceFactory::CreateDevice (D3D12GetInterface). ${copy.reasons["d3d12-factory-limit"]}`);
    reason += ` ${entries.join("; ")}`;
  }
  if (capability.missingFiles.length > 0) reason += ` ${capability.missingFiles.join(", ")}`;
  return copy.summary(phase, state, capability.graphicsApi,
    capability.architecture ? ` / ${capability.architecture}` : "", reason);
}

export function formatGpuLaunchOutcome(outcome: string) {
  switch (outcome) {
    case "startup-configured":
      return uiText.softwareDetail.gpuLaunchOutcome.configured;
    case "provider-ready":
      return uiText.softwareDetail.gpuLaunchOutcome.ready;
    case "pass-through-started":
      return uiText.softwareDetail.gpuLaunchOutcome.systemDefault;
    case "provider-unavailable-fallback":
      return uiText.softwareDetail.gpuLaunchOutcome.switchedToSystemDefault;
    case "bootstrap-failed-fallback":
      return uiText.softwareDetail.gpuLaunchOutcome.switchedToSystemDefault;
    case "provider-timeout":
      return uiText.softwareDetail.gpuLaunchOutcome.prepareTimeout;
    case "debugger-detach-failed":
      return uiText.softwareDetail.gpuLaunchOutcome.startupIncomplete;
    case "recursion-blocked":
      return uiText.softwareDetail.gpuLaunchOutcome.repeatedLaunchStopped;
    case "launch-failed":
      return uiText.softwareDetail.gpuLaunchOutcome.launchFailed;
    default:
      return uiText.softwareDetail.gpuLaunchOutcome.unknown;
  }
}
