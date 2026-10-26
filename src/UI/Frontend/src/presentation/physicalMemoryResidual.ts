import { uiText } from "../text.ts";
import type { PhysicalMemoryResidualFrame } from "../types.ts";

/*
 * 物理内存“系统/驱动保留”的分项名称（docs/runtime_process_attribution_and_io_network_telemetry.md，Physical memory residual）。
 *
 * 后端把保留量拆成带层级标识的分项：资源条和资源事实里是 PID 为 -1 的分项行（attributionKind 就是标识），
 * 进程明细的页帧帧里是 parts[].kind。后端不发名称（池标记行的 name 是原始四字节，这里不读它），
 * 名称、分组和九种语言的文案只在这里出：每个标识对应一个名称，非分页池下的标记、其他标记和未按标记分类
 * 归在“非分页池”一组。池标记标识的后缀是小端 ULONG 的八位大写十六进制（NtFC = 4346744E），
 * 这里还原成四个 ASCII 字符；有字节不可打印时原样显示这八位十六进制，这是确切值，不是替代名称。
 * 认不出的标识原样显示标识本身。
 */

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

/** 这个标识是不是物理内存保留量的分项（资源条分项行的 attributionKind 或页帧帧的 part.kind）。 */
export function isPhysicalMemoryResidualKind(kind: string | null | undefined): kind is string {
  return typeof kind === "string" && kind.startsWith(prefix);
}

/** 池标记：后缀八位十六进制按小端还原成四个字符；有不可打印字节时返回这八位十六进制本身。 */
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

/** 分项所属的组：非分页池下的分项属于“非分页池”，其余不分组。 */
export function physicalMemoryResidualGroup(kind: string): "nonpaged-pool" | null {
  return kind.startsWith(`${nonpagedPool}/`) ? "nonpaged-pool" : null;
}

/** 组的名称（目前只有“非分页池”一组）。 */
export function physicalMemoryResidualGroupName(): string {
  return uiText.physicalMemoryResidual.kinds.nonpagedPool;
}

/** 分项在组内的名称（池标记写作“池标记 NtFC”）；认不出的标识原样返回。 */
export function physicalMemoryResidualLeafName(kind: string): string {
  const copy = uiText.physicalMemoryResidual.kinds;
  const tag = poolTagPattern.exec(kind);
  if (tag) return copy.poolTag(physicalMemoryPoolTagText(tag[1]));
  const key = fixedKinds[kind];
  return key ? copy[key] : kind;
}

/** 分项单独出现时的完整名称：组内分项带上组名（“非分页池 · 池标记 NtFC”）。 */
export function physicalMemoryResidualKindName(kind: string): string {
  const group = physicalMemoryResidualGroup(kind);
  const leaf = physicalMemoryResidualLeafName(kind);
  return group ? uiText.physicalMemoryResidual.grouped(physicalMemoryResidualGroupName(), leaf) : leaf;
}

/** 按后端顺序排列的分项，相邻的同组分项归到一个组里；组内保持原顺序。 */
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

/**
 * 原因代码的说明。固定代码逐个对应；带参数的四类（native-error:N、profile-privilege:0x…、privilege-restore:0x…、
 * superfetch-N:0x…）按前缀对应并写出参数；没有登记的代码给出“没有登记说明”。调用方同时原样显示代码本身。
 */
export function physicalMemoryResidualReason(code: string): string {
  const copy = uiText.physicalMemoryResidual;
  if (Object.hasOwn(copy.reasons, code)) return copy.reasons[code as ReasonKey];
  const native = /^native-error:(-?\d+)$/.exec(code);
  if (native) return copy.reasonFamilies.nativeError(native[1]);
  const profile = /^profile-privilege:(0x[0-9A-F]{8})$/.exec(code);
  if (profile) return copy.reasonFamilies.profilePrivilege(profile[1]);
  const restore = /^privilege-restore:(0x[0-9A-F]{8})$/.exec(code);
  if (restore) return copy.reasonFamilies.privilegeRestore(restore[1]);
  const superfetch = /^superfetch-(\d+):(0x[0-9A-F]{8})$/.exec(code);
  if (superfetch) return copy.reasonFamilies.superfetchQuery(superfetch[1], superfetch[2]);
  return copy.unknownReason;
}

/** 页帧明细表的一行：组标题行，或一个分项（含未分类）的字节数和占保留量的百分比。 */
export type PhysicalMemoryResidualRow =
  | { type: "group"; group: "nonpaged-pool"; label: string }
  | { type: "part"; kind: string; label: string; bytes: number; residualPercent: number | null; grouped: boolean };

/**
 * 进程明细里 physicalMemoryResidual 帧怎么显示，三种状态与帧一一对应：
 * 没有帧是 not-collected（还没有发布过，不是零）；unavailable 带原因说明和原样的原因代码；
 * ready 按帧里的顺序列出分项（相邻的非分页池分项归到一组），末尾是未分类，再给出帧里的保留量、
 * 已用物理内存和已归属的进程私有内存。百分比是行字节数除以帧里的保留量；就绪帧带的原因代码
 * （例如 process-private-below-attributed）同样给出说明和代码。这里不校验帧，帧的自洽由后端和解码器负责。
 */
export type PhysicalMemoryResidualView =
  | { state: "not-collected" }
  | { state: "unavailable"; capturedAt: string; reason: string; reasonCode: string }
  | { state: "ready"; capturedAt: string; rows: PhysicalMemoryResidualRow[]; residualBytes: number; usedBytes: number;
      attributedProcessPrivateBytes: number; reason: { text: string; reasonCode: string } | null };

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
