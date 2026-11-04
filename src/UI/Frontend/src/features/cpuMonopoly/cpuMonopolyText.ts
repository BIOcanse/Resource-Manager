import { formatTrimmedDigits } from "../../presentation/interfaceNumbers.ts";
import { uiText } from "../../text.ts";

// Localize CPU protection codes and values.
// Display unknown backend codes unchanged.

function lookup(table: Readonly<Record<string, string>>, code: string): string {
  return Object.hasOwn(table, code) ? table[code]! : code;
}

export function cpuMonopolyActionReasonText(code: string): string {
  return lookup(uiText.cpuMonopoly.actionReasons, code);
}

export function cpuMonopolyInactiveReasonText(code: string): string {
  return lookup(uiText.cpuMonopoly.inactiveReasons, code);
}

export function cpuMonopolyExclusionReasonText(code: string): string {
  return lookup(uiText.cpuMonopoly.exclusionReasons, code);
}

/** Display unknown priority classes unchanged. */
export function cpuMonopolyPriorityText(priorityClass: string): string {
  return lookup(uiText.cpuMonopoly.priorities, priorityClass);
}

export function cpuMonopolyPercentText(value: number): string {
  return uiText.cpuMonopoly.percent(formatTrimmedDigits(value, 1));
}

export function cpuMonopolySecondsText(value: number): string {
  return uiText.cpuMonopoly.seconds(formatTrimmedDigits(value, 1));
}
