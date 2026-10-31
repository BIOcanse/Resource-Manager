import type { CrashDumpDiagnostics } from "../../types.ts";
import {
  ResponseDecodeError, requireArray, requireBoolean, requireNonNegativeSafeInteger, requireNullable, requireOneOf, requireRecord,
  requireString, requireStringArray
} from "../../frontendRuntime/request/ResponseDecoder.ts";

function requirePositiveInteger(value: unknown, path: string): number {
  const number = requireNonNegativeSafeInteger(value, path);
  if (number < 1) throw new ResponseDecodeError(path, "Expected a positive integer");
  return number;
}

/**
 * 设置结果里的 crashDumps 诊断（crash-reports wire-contract.md “Settings”）：实际生效的模式和它的来源、
 * 转储目录、RM 自己的映像名，以及注册表里读到的 LocalDumps 键。前端只显示，不推算期望值。
 */
export function decodeCrashDumpDiagnostics(value: unknown, path = "$.crashDumps"): CrashDumpDiagnostics {
  const record = requireRecord(value, path);
  return Object.freeze({
    state: requireOneOf(record.state, path + ".state", ["notApplied", "applied", "mismatch", "failed", "skipped"] as const),
    effectiveMode: requireOneOf(record.effectiveMode, path + ".effectiveMode", ["ask", "on", "off"] as const),
    modeSource: requireOneOf(record.modeSource, path + ".modeSource", ["user", "channelDefault"] as const),
    defaultMode: requireOneOf(record.defaultMode, path + ".defaultMode", ["ask", "on", "off"] as const),
    channel: requireOneOf(record.channel, path + ".channel", ["beta", "stable"] as const),
    dumpFolder: requireString(record.dumpFolder, path + ".dumpFolder"),
    keepCountMaximum: requirePositiveInteger(record.keepCountMaximum, path + ".keepCountMaximum"),
    images: Object.freeze(requireStringArray(record.images, path + ".images")),
    actual: requireNullable(record.actual, path + ".actual", (items, itemsPath) =>
      Object.freeze(requireArray(items, itemsPath).map((item, index) => {
        const entryPath = `${itemsPath}[${index}]`;
        const entry = requireRecord(item, entryPath);
        return Object.freeze({
          image: requireString(entry.image, entryPath + ".image"),
          present: requireBoolean(entry.present, entryPath + ".present"),
          ownedByResourceManager: requireBoolean(entry.ownedByResourceManager, entryPath + ".ownedByResourceManager"),
          dumpFolder: requireNullable(entry.dumpFolder, entryPath + ".dumpFolder", requireString),
          dumpType: requireNullable(entry.dumpType, entryPath + ".dumpType", requireNonNegativeSafeInteger),
          customDumpFlags: requireNullable(entry.customDumpFlags, entryPath + ".customDumpFlags", requireNonNegativeSafeInteger),
          dumpCount: requireNullable(entry.dumpCount, entryPath + ".dumpCount", requireNonNegativeSafeInteger)
        });
      }))),
    skipReason: requireNullable(record.skipReason, path + ".skipReason", requireString),
    writeError: requireNullable(record.writeError, path + ".writeError", requireString),
    readError: requireNullable(record.readError, path + ".readError", requireString)
  });
}
