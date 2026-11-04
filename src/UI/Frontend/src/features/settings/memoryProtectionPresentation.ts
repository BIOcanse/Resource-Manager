import { uiText } from "../../text.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { formatBytes, formatSignedBytes } from "../../presentation/byteUnits.ts";
import type {
  HostMemoryActionOutcome, HostMemoryActionRecord, HostMemoryDecisionReason
} from "../../data/memoryProtection/memoryProtectionTypes.ts";

// Localize known backend codes and preserve their parameters.
// Return unknown codes unchanged.

const wholeClusterPatterns = [
  /^reserve-size-must-be-whole-clusters:cluster-bytes=(\d+)$/,
  /^reserve-size-must-be-a-whole-allocation-unit; cluster-bytes=(\d+)$/
];

function knownReason(code: string): string | null {
  const reasons: Readonly<Record<string, string>> = uiText.memoryProtection.reasons;
  return Object.hasOwn(reasons, code) ? reasons[code]! : null;
}

/** Format exact bytes with locale grouping and B, without unit scaling. */
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

/** Candidates and skipped tree members share exclusion reasons. */
export function memoryProtectionExclusionText(reason: string): string {
  const reasons: Readonly<Record<string, string>> = uiText.memoryProtection.exclusionReasons;
  return Object.hasOwn(reasons, reason) ? reasons[reason]! : reason;
}

/** Preserve signed backend byte values. */
export function bytesAsSent(bytes: number): string {
  return bytes < 0 ? formatSignedBytes(bytes, "memory") : formatBytes(bytes, "memory");
}

export function memoryProtectionDecisionText(reason: HostMemoryDecisionReason): string {
  const reasons: Readonly<Record<string, string>> = uiText.memoryProtection.decisionReasons;
  return Object.hasOwn(reasons, reason) ? reasons[reason]! : reason;
}

/** Skipped members use exclusion reasons; other records use action reasons. */
export function memoryProtectionRecordReasonText(record: Pick<HostMemoryActionRecord, "outcome" | "reason">): string | null {
  if (record.reason === null) return null;
  return record.outcome === "excluded"
    ? memoryProtectionExclusionText(record.reason)
    : memoryProtectionReasonText(record.reason);
}

export interface MemoryProtectionActionGroup {
  readonly actionId: string;
  /** Preserve backend newest-first record order. */
  readonly records: readonly HostMemoryActionRecord[];
}

/** Group by actionId in first-occurrence order, preserving order within groups. */
export function groupMemoryProtectionActions(actions: readonly HostMemoryActionRecord[]): MemoryProtectionActionGroup[] {
  const groups = new Map<string, HostMemoryActionRecord[]>();
  for (const record of actions) {
    const group = groups.get(record.actionId);
    if (group) group.push(record);
    else groups.set(record.actionId, [record]);
  }
  return [...groups].map(([actionId, records]) => ({ actionId, records }));
}
