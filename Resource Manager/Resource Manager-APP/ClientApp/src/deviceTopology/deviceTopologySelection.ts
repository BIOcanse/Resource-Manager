import { createSignal } from "solid-js";

const [requestedPortId, setRequestedPortId] = createSignal<string | null>(null);

export const requestedDeviceTopologyPortId = requestedPortId;

export function clearRequestedDeviceTopologyPort(): void {
  setRequestedPortId(null);
}

export function requestDeviceTopologySelection(targetKey: string): void {
  const normalized = targetKey.trim().replace(/^device:/i, "");
  setRequestedPortId(normalized || null);
}
