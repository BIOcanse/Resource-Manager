import { uiText } from "../../text.ts";
import type { AppCopy } from "../../i18n/copy/index.ts";

type ControlPresentationCopy = AppCopy["control"]["presentation"];

export function controlCapabilityLabel(
  capability: { id: string; label: string }, text: ControlPresentationCopy = uiText.control.presentation
): string {
  if (capability.id === "cpu.power-limit" && capability.label.includes("PL1")) {
    return text.powerLimitPl1;
  }
  return text.labels[capability.id] ?? capability.label;
}

export function controlDisplayText(value: string, text: ControlPresentationCopy = uiText.control.presentation): string {
  const fan = /^风扇\s*(\d+)$/.exec(value);
  const controller = /^显卡风扇控制器（GPU(\d+)）$/.exec(value);
  return fan ? text.fanName(fan[1]) : controller ? text.gpuFanControllerName(controller[1]) : text.text[value] ?? value;
}
