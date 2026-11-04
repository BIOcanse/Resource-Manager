import { uiText } from "../text.ts";
import type { PhysicalMemoryResidualFrame } from "../types.ts";

/* Localize residual attributionKind/parts[].kind IDs. Group nonpaged-pool parts together. Pool-tag suffixes encode a little-endian ULONG as eight hex digits; decode printable ASCII or retain the hex. Preserve unknown IDs. */

const prefix = "system-memory/";
const nonpagedPool = `${prefix}nonpaged-pool`;
const poolTagPattern = /^system-memory\/nonpaged-pool\/tag\/([0-9A-F]{8})$/;

type KindCopy = typeof uiText.physicalMemoryResidual.kinds;
type FixedKindKey = Exclude<keyof KindCopy, "poolTag">;

const fixedKinds: Readonly<Record<string, FixedKindKey>> = {
  [nonpagedPool]: "nonpagedPool",
  [`${nonpagedPool}/other-tags`]: "otherPoolTags",
  [`${nonpagedPool}/unclassified`]: "unclassifiedPool",
  [`${prefix}paged-pool-resident`]: "pagedPoolResident",
  [`${prefix}driver-resident`]: "driverResident",
  [`${prefix}code-resident`]: "codeResident",
  [`${prefix}unclassified`]: "unclassified",
  [`${prefix}mapped-file`]: "mappedFile",
  [`${prefix}shareable`]: "shareable",
  [`${prefix}page-table`]: "pageTable",
  [`${prefix}system-pte`]: "systemPte",
  [`${prefix}metafile`]: "metafile",
  [`${prefix}driver-locked`]: "driverLocked",
  [`${prefix}kernel-stack`]: "kernelStack",
  [`${prefix}working-set-internals`]: "workingSetInternals",
  [`${prefix}modified`]: "modified",
  [`${prefix}process-private-not-attributed`]: "processPrivateNotAttributed"
};

/** Whether attributionKind or part.kind identifies a physical-memory residual part. */
export function isPhysicalMemoryResidualKind(kind: string | null | undefined): kind is string {
  return typeof kind === "string" && kind.startsWith(prefix);
}

/** Decode the eight-hex-digit pool tag as little-endian ASCII; retain hex if any byte is unprintable. */
export function physicalMemoryPoolTagText(hex: string): string {
  const value = Number.parseInt(hex, 16);
  let text = "";
  for (let index = 0; index < 4; index++) {
    const byte = (value >>> (8 * index)) & 0xff;
    if (byte < 32 || byte > 126) return hex;
    text += String.fromCharCode(byte);
  }
  return text;
}

/** Nonpaged-pool parts share a group; other parts are ungrouped. */
export function physicalMemoryResidualGroup(kind: string): "nonpaged-pool" | null {
  return kind.startsWith(`${nonpagedPool}/`) ? "nonpaged-pool" : null;
}

/** Localized residual-group name; currently only nonpaged pool. */
export function physicalMemoryResidualGroupName(): string {
  return uiText.physicalMemoryResidual.kinds.nonpagedPool;
}

/** Name within a group; preserve unknown IDs. */
export function physicalMemoryResidualLeafName(kind: string): string {
  const copy = uiText.physicalMemoryResidual.kinds;
  const tag = poolTagPattern.exec(kind);
  if (tag) return copy.poolTag(physicalMemoryPoolTagText(tag[1]));
  const key = fixedKinds[kind];
  return key ? copy[key] : kind;
}

/** Standalone part name, prefixed by its group when grouped. */
export function physicalMemoryResidualKindName(kind: string): string {
  const group = physicalMemoryResidualGroup(kind);
  const leaf = physicalMemoryResidualLeafName(kind);
  return group ? uiText.physicalMemoryResidual.grouped(physicalMemoryResidualGroupName(), leaf) : leaf;
}

/** Preserve backend order; combine only adjacent parts belonging to the same group. */
function groupPhysicalMemoryResidualParts<T extends { kind: string }>(parts: readonly T[]): Array<
  { group: null; part: T } | { group: "nonpaged-pool"; parts: T[] }> {
  const rows: Array<{ group: null; part: T } | { group: "nonpaged-pool"; parts: T[] }> = [];
  for (const part of parts) {
    const group = physicalMemoryResidualGroup(part.kind);
    const last = rows.at(-1);
    if (group && last?.group === group) last.parts.push(part);
    else rows.push(group ? { group, parts: [part] } : { group: null, part });
  }
  return rows;
}

type ReasonKey = keyof typeof uiText.physicalMemoryResidual.reasons;

/** Resolve fixed reason codes and parameterized superfetch-17, superfetch-6, profile-privilege, privilege-restore, and native-error families. Error parameters remain data. Unknown codes return null for verbatim display. */
export function physicalMemoryResidualReason(code: string): string | null {
  const copy = uiText.physicalMemoryResidual;
  if (Object.hasOwn(copy.reasons, code)) return copy.reasons[code as ReasonKey];
  const family = /^(superfetch-17|superfetch-6|profile-privilege|privilege-restore):(0x[0-9A-F]{8})$/.exec(code);
  if (family?.[1] === "superfetch-17") return copy.reasonFamilies.superfetchRanges(family[2]);
  if (family?.[1] === "superfetch-6") return copy.reasonFamilies.superfetchPages(family[2]);
  if (family?.[1] === "profile-privilege") return copy.reasonFamilies.profilePrivilege(family[2]);
  if (family?.[1] === "privilege-restore") return copy.reasonFamilies.privilegeRestore(family[2]);
  const native = /^native-error:(\d+)$/.exec(code);
  if (native) return copy.reasonFamilies.nativeError(native[1]);
  return null;
}

/** Group header or part bytes and percentage of reserved memory, including unclassified bytes. */
export type PhysicalMemoryResidualRow =
  | { type: "group"; group: "nonpaged-pool"; label: string }
  | { type: "part"; kind: string; label: string; bytes: number; residualPercent: number | null; grouped: boolean };

/** Missing frame means not-collected, never zero. Unavailable frames show reasons; ready frames retain part order and append unclassified bytes. Percentages divide by reserved bytes. Backend and decoder own consistency; known and unknown reason codes remain visible. */
export type PhysicalMemoryResidualView =
  | { state: "not-collected" }
  | { state: "unavailable"; capturedAt: string; reason: string | null; reasonCode: string }
  | { state: "ready"; capturedAt: string; rows: PhysicalMemoryResidualRow[]; residualBytes: number; usedBytes: number;
      attributedProcessPrivateBytes: number; reason: { text: string | null; reasonCode: string } | null };

export function physicalMemoryResidualView(frame: PhysicalMemoryResidualFrame | undefined): PhysicalMemoryResidualView {
  if (!frame) return { state: "not-collected" };
  if (frame.status === "unavailable") {
    return { state: "unavailable", capturedAt: frame.capturedAt, reason: physicalMemoryResidualReason(frame.reasonCode), reasonCode: frame.reasonCode };
  }
  const percent = (bytes: number) => frame.residualBytes > 0 ? bytes * 100 / frame.residualBytes : null;
  const part = (kind: string, label: string, bytes: number, grouped: boolean): PhysicalMemoryResidualRow =>
    ({ type: "part", kind, label, bytes, residualPercent: percent(bytes), grouped });
  const rows: PhysicalMemoryResidualRow[] = [];
  for (const entry of groupPhysicalMemoryResidualParts(frame.parts)) {
    if (entry.group === null) {
      rows.push(part(entry.part.kind, physicalMemoryResidualLeafName(entry.part.kind), entry.part.bytes, false));
      continue;
    }
    rows.push({ type: "group", group: entry.group, label: physicalMemoryResidualGroupName() });
    for (const child of entry.parts) rows.push(part(child.kind, physicalMemoryResidualLeafName(child.kind), child.bytes, true));
  }
  rows.push(part("unclassified", uiText.physicalMemoryResidual.kinds.unclassified, frame.unclassifiedBytes, false));
  return {
    state: "ready", capturedAt: frame.capturedAt, rows, residualBytes: frame.residualBytes, usedBytes: frame.usedBytes,
    attributedProcessPrivateBytes: frame.attributedProcessPrivateBytes,
    reason: frame.reasonCode ? { text: physicalMemoryResidualReason(frame.reasonCode), reasonCode: frame.reasonCode } : null
  };
}
