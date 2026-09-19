import { currentLanguage } from "../text.ts";

const englishLabels: Record<string, string> = {
  "gpu.core-clock-offset": "Core clock offset",
  "gpu.memory-clock-offset": "Memory clock offset",
  "gpu.core-clock-minimum": "Minimum core clock",
  "gpu.core-clock-maximum": "Maximum core clock",
  "gpu.memory-clock-minimum": "Minimum memory clock",
  "gpu.memory-clock-maximum": "Maximum memory clock",
  "gpu.core-voltage-offset": "Core voltage offset",
  "gpu.power-limit": "Power limit",
  "gpu.temperature-limit": "Temperature limit",
  "gpu.slowdown-temperature": "Thermal slowdown threshold",
  "gpu.shutdown-temperature": "Thermal shutdown threshold",
  "gpu.power-management-mode": "Power management mode",
  "gpu.frame-rate-limit": "Frame rate limit",
  "gpu.ctgp-offset": "cTGP offset",
  "gpu.dynamic-boost-enabled": "Dynamic Boost",
  "gpu.dynamic-boost-offset": "Dynamic Boost allowance",
  "gpu.curve-optimizer": "Curve Optimizer offset",
  "cpu.power-limit": "Sustained power limit",
  "cpu.slow-power-limit": "Short-term power limit",
  "cpu.fast-power-limit": "Fast power limit",
  "cpu.tdc-limit": "Sustained current limit (TDC)",
  "cpu.edc-limit": "Peak current limit (EDC)",
  "cpu.temperature-limit": "Temperature limit",
  "cpu.pbo-scalar": "PBO scalar",
  "cpu.curve-optimizer": "Curve Optimizer offset",
  "cpu.short-power-limit": "Short-term power limit (PL2)",
  "cpu.power-limit-window": "Sustained power time window",
  "cpu.short-power-limit-window": "Short-term power time window",
  "cpu.power-limit-mmio": "Power limit (MMIO mirror)",
  "cpu.temperature-offset": "Thermal limit reduction",
  "cpu.core-voltage-offset": "Core voltage offset",
  "cpu.cache-voltage-offset": "Cache voltage offset",
  "cpu.igpu-voltage-offset": "Integrated GPU voltage offset",
  "cpu.system-agent-voltage-offset": "System agent voltage offset",
  "cpu.turbo-ratio-limit": "Turbo ratio limit",
  "cpu.turbo-enabled": "Turbo Boost",
  "cpu.energy-performance-preference": "Energy-performance preference",
  "cpu.igpu-power-balance": "CPU / integrated GPU power balance",
  "fan.rpm": "Fan speed",
  "fan.curve": "Fan speed curve",
  "fan.duty": "Fixed fan speed",
  "fan.lock-maximum": "Maximum cooling"
};

const englishText: Record<string, string> = {
  "档": "steps",
  "硬件写入辅助进程": "Hardware control helper",
  "风扇控制核心": "Fan control core",
  "整机固件接口": "System firmware interface",
  "只读，写入未接入。": "Read-only; write support is not implemented.",
  "驱动未提供这一温度阈值的可写范围。": "The driver does not expose a writable range for this temperature threshold.",
  "转速为监测值。": "Fan speed is a monitored reading.",
  "命令和电流墙的对应关系还没对准，写错会把处理器掐住，先不开放。": "The current-limit commands have not been verified. Writing an incorrect command could stall the processor, so adjustment is unavailable.",
  "还认不出这颗处理器温度墙的当前值，没有回读就不写。": "The current thermal limit cannot be read on this processor. Adjustment is unavailable without readback.",
  "这几项是笔记本整机固件的功率预算，台式机上没有。": "These power budgets belong to laptop system firmware and are not available on desktops.",
  "Intel 独显的调节不在本软件的范围内。": "Tuning Intel discrete GPUs is outside this application's scope.",
  "笔记本上的 AMD 独显不在本软件的范围内。": "Tuning AMD discrete GPUs in laptops is outside this application's scope."
};

export function controlCapabilityLabel(
  capability: { id: string; label: string }, language = currentLanguage()
): string {
  if (language !== "en-US") return capability.label;
  if (capability.id === "cpu.power-limit" && capability.label.includes("PL1")) {
    return "Sustained power limit (PL1)";
  }
  return englishLabels[capability.id] ?? capability.label;
}

export function controlDisplayText(value: string, language = currentLanguage()): string {
  if (language !== "en-US") return value;
  const fan = /^风扇\s*(\d+)$/.exec(value);
  return fan ? `Fan ${fan[1]}` : englishText[value] ?? value;
}
