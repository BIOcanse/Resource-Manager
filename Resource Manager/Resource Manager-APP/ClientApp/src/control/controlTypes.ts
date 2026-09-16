/**
 * 控制面的线上形状。
 *
 * 一个可控对象就是一块卡、一个风扇、一颗 CPU。身份沿用监控侧的标识，
 * 所以同一块卡在监控页和控制页是同一个东西。
 */
export interface ControlObjectPlatform {
  operatingSystem: string;
  vendor: string;
}

export interface ControlNumberRange {
  minimum: number;
  maximum: number;
  step: number;
  unit: string;
  /** 厂商/系统默认值，「恢复默认」回到这里。读不到就是 null。 */
  defaultValue: number | null;
}

export interface ControlCapability {
  id: string;
  label: string;
  valueKind: "toggle" | "number" | "curve";
  /** 现在能不能用。不能用时一定带着原因。 */
  supported: boolean;
  unavailableReason: string | null;
  requiredComponentId: string | null;
  requiredComponentName: string | null;
  range: ControlNumberRange | null;
}

export interface ControlObject {
  id: string;
  kind: string;
  displayName: string;
  platform: ControlObjectPlatform;
  capabilities: readonly ControlCapability[];
  detail: string | null;
  /** 一项都控不了。界面据此整体标灰。 */
  isControllable: boolean;
}

export interface ControlObjectCatalog {
  objects: readonly ControlObject[];
  readAt: string;
}
