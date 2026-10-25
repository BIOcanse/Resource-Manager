import { uiText } from "../text.ts";

const noteKeys: Record<string, keyof typeof uiText.componentCatalog.installNotes> = {
  "shared-webview2-runtime": "webRuntime",
  "amd-smu-pawnio-provider": "amdSmu",
  "intel-msr-pawnio-provider": "intelMsr",
  "hardware-sensors-pawnio-provider": "sensorDriver",
  "hardware-bridge": "hardwareBridge",
  "librehardwaremonitor-provider": "hardwareMonitor",
  "notebook-oem-fan-provider": "notebookFan",
  "nvidia-nvml-provider": "driverRuntime",
  "nvidia-nvapi-provider": "driverRuntime",
  "amd-adlx-provider": "driverRuntime"
};

export function componentInstallNote(id: unknown) {
  const key = noteKeys[String(id ?? "").toLowerCase()];
  return key ? uiText.componentCatalog.installNotes[key] : "";
}

const capabilityKeys: Record<string, keyof typeof uiText.componentCatalog.capabilities> = {
  power: "power", voltage: "voltage", current: "current", temperature: "temperature", fan: "fan",
  "smu-pm-table": "smuPmTable", stapm: "stapm", "per-core": "perCore", "webview2-runtime": "webRuntime",
  pl1: "pl1", pl2: "pl2", "time-window": "timeWindow", "voltage-offset": "voltageOffset",
  "hardware-monitor-core": "hardwareMonitor", "memory-temperature": "memoryTemperature",
  "notebook-oem-fan.cpu-rpm": "cpuFanRpm", "notebook-oem-fan.cpu-percent": "cpuFanPercent",
  "notebook-oem-fan.gpu-rpm": "gpuFanRpm", "notebook-oem-fan.gpu-percent": "gpuFanPercent",
  "notebook-oem-fan.vendor-adapters": "vendorAdapters",
  "nvidia-gpu-clocks": "gpuClocks", "nvidia-gpu-memory": "gpuMemory", "nvidia-gpu-power": "gpuPower",
  "nvidia-gpu-temperature": "gpuTemperature", "nvidia-gpu-cooler": "gpuCooler",
  "nvidia-gpu-voltage": "gpuVoltage", "nvidia-gpu-current": "gpuCurrent",
  "amd-gpu-clocks": "gpuClocks", "amd-gpu-power": "gpuPower", "amd-gpu-board-power": "gpuBoardPower",
  "amd-gpu-temperature": "gpuTemperature", "amd-gpu-hotspot-temperature": "gpuHotspot",
  "amd-gpu-intake-temperature": "gpuIntake", "amd-gpu-fan": "gpuFan", "amd-gpu-voltage": "gpuVoltage"
};

export function componentCapabilityLabel(id: unknown) {
  const value = String(id ?? "");
  const key = capabilityKeys[value] ?? capabilityKeys[value.slice(value.lastIndexOf(".") + 1)];
  return key ? uiText.componentCatalog.capabilities[key] : uiText.componentCatalog.unknownCapability;
}

export function componentVersionFailure(code: string | null | undefined) {
  if (!code) return null;
  const failures = uiText.componentCatalog.versionFailures;
  const text = failures[code as keyof typeof failures];
  if (text) return text;
  console.warn("[components] Unknown version availability code", code);
  return failures["catalog-failure"];
}
