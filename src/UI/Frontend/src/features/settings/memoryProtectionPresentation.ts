import { uiText } from "../../text.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { formatBytes, formatSignedBytes } from "../../presentation/byteUnits.ts";
import type { HostMemoryActionOutcome, HostMemoryExclusionReason } from "../../data/memoryProtection/memoryProtectionTypes.ts";

// 主机内存保护的后端原因码 → 界面文字。只翻译，不改写：固定码查表，带参数的码把参数原样放进文案，
// 认不出的码（系统或磁盘异常原文、新加的码）原样返回，界面照原样显示。

const wholeClusterPatterns = [
  /^reserve-size-must-be-whole-clusters:cluster-bytes=(\d+)$/,
  /^reserve-size-must-be-a-whole-allocation-unit; cluster-bytes=(\d+)$/
];

function knownReason(code: string): string | null {
  const reasons: Readonly<Record<string, string>> = uiText.memoryProtection.reasons;
  return Object.hasOwn(reasons, code) ? reasons[code]! : null;
}

/** 一个确切的字节数（簇大小这类必须按原值看的量）：按界面语言分组的整数加字节符号 B，不换算单位。 */
export function exactByteCount(bytes: number): string {
  return `${formatInterfaceNumber(bytes)} B`;
}

export function memoryProtectionReasonText(code: string): string {
  const copy = uiText.memoryProtection;
  const known = knownReason(code);
  if (known !== null) return known;
  for (const pattern of wholeClusterPatterns) {
    const match = pattern.exec(code);
    if (match) return copy.wholeClusters(exactByteCount(Number(match[1])));
  }
  const location = /^invalid-reserve-location:([\s\S]+)$/.exec(code);
  if (location) return copy.invalidLocation(knownReason(location[1]!) ?? location[1]!);
  const suspend = /^ntstatus-([0-9a-f]{8})$/.exec(code);
  if (suspend) return copy.suspendStatus(`0x${suspend[1]}`);
  const terminate = /^terminate-failed-(\d+)$/.exec(code);
  if (terminate) return copy.terminateFailed(terminate[1]!);
  const release = /^private-memory-decommit-limited chunks=(\d+) bytes=(\d+)(?:;[\s\S]*)?$/.exec(code);
  if (release) return copy.releaseLimited(formatInterfaceNumber(Number(release[1])), formatBytes(Number(release[2]), "memory"));
  return code;
}

export function memoryProtectionOutcomeText(outcome: HostMemoryActionOutcome): string {
  const outcomes: Readonly<Record<string, string>> = uiText.memoryProtection.outcomes;
  return Object.hasOwn(outcomes, outcome) ? outcomes[outcome]! : outcome;
}

export function memoryProtectionExclusionText(reason: HostMemoryExclusionReason): string {
  const reasons: Readonly<Record<string, string>> = uiText.memoryProtection.exclusionReasons;
  return Object.hasOwn(reasons, reason) ? reasons[reason]! : reason;
}

/** 后端给的可正可负的字节量（提交量余量、增长量）照原值显示：负数带负号。 */
export function bytesAsSent(bytes: number): string {
  return bytes < 0 ? formatSignedBytes(bytes, "memory") : formatBytes(bytes, "memory");
}
