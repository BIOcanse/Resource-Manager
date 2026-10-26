import type { DeviceTopologyDisplayConnection, DeviceTopologyPort } from "../../types.ts";

/** 当前连接由后端 targetAvailable 给定，与输出是否 active、是否保留声明资料分开。 */
export function connectedDisplay(port: DeviceTopologyPort | null | undefined): DeviceTopologyDisplayConnection | null {
  return port?.display?.targetAvailable === true ? port.display : null;
}
