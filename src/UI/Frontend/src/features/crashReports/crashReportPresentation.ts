import type { CrashDumpDiagnostics } from "../../types.ts";
import type { CrashDumpsCopy } from "../../i18n/settingsTypes.ts";
import type { CrashReport, CrashReportFault } from "../../data/crashReports/crashReports.ts";

// 崩溃转储的显示（docs/crash_dumps_2026-10-31.md）：只把后端给的事实换成当前语言的文字，不推算状态。

/** 实际生效的模式和来源；beta 版默认开启时给出“一键关闭”。没有诊断时为 null。 */
export function crashDumpModeView(diagnostics: CrashDumpDiagnostics | undefined, copy: CrashDumpsCopy) {
  if (!diagnostics) return null;
  const source = diagnostics.modeSource === "user"
    ? copy.sourceUser
    : copy.channelDefault(copy.channelNames[diagnostics.channel], copy.defaultModeNames[diagnostics.defaultMode]);
  return {
    effective: copy.effective(copy.effectiveModes[diagnostics.effectiveMode], source),
    offerTurnOff: diagnostics.effectiveMode === "on" && diagnostics.modeSource === "channelDefault"
  };
}

export interface CrashDumpImageView {
  readonly image: string;
  readonly state: string;
  readonly detail: string | null;
}

/** 期望和实际：状态一句、跳过或失败的原因、注册表里每个映像的实际键。 */
export function crashDumpStatusView(diagnostics: CrashDumpDiagnostics | undefined, copy: CrashDumpsCopy) {
  if (!diagnostics) return { status: copy.status.notApplied, reasons: [] as string[], images: [] as CrashDumpImageView[] };
  const reasons: string[] = [];
  if (diagnostics.skipReason !== null) {
    const known = (copy.skipReasons as Record<string, string>)[diagnostics.skipReason];
    reasons.push(known ?? copy.skipReasonOther(diagnostics.skipReason));
  }
  if (diagnostics.writeError !== null) reasons.push(copy.writeFailed(diagnostics.writeError));
  if (diagnostics.readError !== null) reasons.push(copy.readFailed(diagnostics.readError));
  const images = (diagnostics.actual ?? []).map((entry): CrashDumpImageView => {
    if (!entry.present) return { image: entry.image, state: copy.imageStates.absent, detail: null };
    if (!entry.ownedByResourceManager) return { image: entry.image, state: copy.imageStates.foreign, detail: null };
    return {
      image: entry.image,
      state: copy.imageStates.written,
      detail: copy.actualDetail(dumpTypeLabel(entry.dumpType, copy), entry.dumpCount,
        entry.dumpFolder ?? "—")
    };
  });
  return { status: copy.status[diagnostics.state], reasons, images };
}

/** WER LocalDumps 的 DumpType：0 是自定义（小型转储用的标志），2 是完整转储。 */
function dumpTypeLabel(value: number | null, copy: CrashDumpsCopy): string {
  if (value === null) return "—";
  if (value === 0) return copy.dumpTypeOptions.mini.label;
  if (value === 2) return copy.dumpTypeOptions.full.label;
  return copy.dumpTypeOther(value);
}

/** 故障摘要：模块和偏移、异常码、说明；没有说明时取托管栈第一行。都没有时说明事件日志里没有对应记录。 */
export function crashFaultSummary(fault: CrashReportFault | null, copy: CrashDumpsCopy): string {
  if (!fault) return copy.faultUnknown;
  const parts: string[] = [];
  if (fault.module) parts.push(fault.offset ? `${fault.module} ${fault.offset}` : fault.module);
  if (fault.exceptionCode) parts.push(fault.exceptionCode);
  const stackLine = fault.managedStack?.split(/\r?\n/).map(line => line.trim()).find(line => line.length > 0) ?? null;
  const text = fault.description ?? stackLine;
  if (text) parts.push(text);
  return parts.length > 0 ? parts.join(" · ") : copy.faultUnknown;
}

/** 弹窗和“打开所在文件夹”选中的本地文件：报告包已生成时是报告包，否则是还在的转储文件；都没有时为 null。 */
export function crashReportLocalFile(report: CrashReport): string | null {
  if (report.package) return report.package.path;
  return report.dumpStatus === "saved" && report.dump ? report.dump.path : null;
}

/** 设置页“转储文件”一栏：保存着时是路径，没开转储或文件已不在时是说明。 */
export function crashReportDumpText(report: CrashReport, copy: CrashDumpsCopy): { readonly path: string | null; readonly note: string | null } {
  switch (report.dumpStatus) {
    case "saved": return { path: report.dump?.path ?? null, note: null };
    case "notEnabled": return { path: null, note: copy.dumpNotEnabled };
    case "missing": return { path: report.dump?.path ?? null, note: copy.dumpMissing };
  }
}
