import type { DeviceTopologyDisplayConnection, DeviceTopologyPort } from "../../types.ts";

/** Backend targetAvailable defines connection independently of output activity or retained display declarations. */
export function connectedDisplay(port: DeviceTopologyPort | null | undefined): DeviceTopologyDisplayConnection | null {
  return port?.display?.targetAvailable === true ? port.display : null;
}
