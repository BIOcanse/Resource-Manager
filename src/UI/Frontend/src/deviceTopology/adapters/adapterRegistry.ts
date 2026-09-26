import { audioDeviceAdapter } from "./audioDeviceAdapter.ts";
import { cameraAdapter } from "./cameraAdapter.ts";
import { externalGpuDockAdapter } from "./externalGpuDockAdapter.ts";
import { externalStorageAdapter } from "./externalStorageAdapter.ts";
import { genericDeviceAdapter } from "./genericDeviceAdapter.ts";
import { dockAdapter, usbHubAdapter } from "./hubAdapter.ts";
import { keyboardAdapter, mouseAdapter } from "./inputDeviceAdapters.ts";
import {
  bluetoothAdapter,
  graphicsAdapter,
  internalControllerAdapter,
  networkAdapter
} from "./internalDeviceAdapters.ts";
import { mobileDeviceAdapter } from "./mobileDeviceAdapter.ts";
import { monitorAdapter } from "./monitorAdapter.ts";
import { powerInputAdapter } from "./powerInputAdapter.ts";
import { internalDeviceFacts } from "./adapterEvidence.ts";
import type { DeviceAdapter, DeviceAdapterContext, SpecializedDeviceModel } from "./types";

const adapters: readonly DeviceAdapter[] = [
  monitorAdapter,
  externalGpuDockAdapter,
  dockAdapter,
  usbHubAdapter,
  externalStorageAdapter,
  keyboardAdapter,
  mouseAdapter,
  cameraAdapter,
  mobileDeviceAdapter,
  audioDeviceAdapter,
  powerInputAdapter,
  graphicsAdapter,
  networkAdapter,
  bluetoothAdapter,
  internalControllerAdapter,
  genericDeviceAdapter
];

export function resolveSpecializedDevice(context: DeviceAdapterContext): SpecializedDeviceModel {
  const adapter = adapters.find((candidate) => candidate.matches(context)) ?? genericDeviceAdapter;
  const model = adapter.createModel(context);
  const internalFacts = internalDeviceFacts(context);
  return internalFacts ? { ...model, internalFacts } as SpecializedDeviceModel : model;
}

export const deviceAdapterOrder = adapters.map((adapter) => adapter.id);

export type {
  DeviceAdapterKind,
  DeviceAdapterIconKind,
  SpecializedDeviceModel,
  SpecializedDeviceSummaryField
} from "./types";
