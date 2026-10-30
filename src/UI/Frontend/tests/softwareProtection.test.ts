import assert from "node:assert/strict";
import { protectedTargetListDecoder } from "../src/data/optimization/optimizationReportsDecoder.ts";
import { resolveSoftwareProtection } from "../src/data/optimization/softwareProtection.ts";

// 软件详情的保护开关按后端 OptimizationProtectionTable.Resolve 的软件这一层决定：软件编号的条目，没有再看软件类型的条目。
const softwareId = "windows-service:Audiosrv";
const kindEntry = entry({ id: "software_kind:software-kind:windows_service", targetType: "software_kind",
  targetKey: "software-kind:windows_service", softwareId: null, softwareKind: "windows_service", protectionSource: "system_default" });
const catalogEntry = entry({ id: "software:windows-service:audiosrv", protectionSource: "software_catalog" });
const switchedOff = entry({ id: "software:windows-service:audiosrv", protectionSource: "software_detail", protectionLevel: 0,
  defaultProtectionLevel: 1, defaultProtectionSource: "software_catalog" });
const processEntry = entry({ id: "process:process-name:audiodg", targetType: "process", targetKey: "process-name:audiodg",
  softwareId, processName: "audiodg", protectionSource: "software_catalog" });

const resolve = (entries: readonly object[], kind: string | null = "windows_service") =>
  resolveSoftwareProtection(protectedTargetListDecoder.decode(entries), softwareId, kind);

assert.equal(resolve([]), null, "No entry applies: nothing decides");
assert.equal(resolve([kindEntry])?.protectionSource, "system_default", "Only the kind entry applies");
assert.equal(resolve([kindEntry], "other"), null, "A kind entry of another kind does not apply");
assert.equal(resolve([kindEntry], null), null, "Without a kind only software entries apply");
assert.equal(resolve([kindEntry, catalogEntry])?.protectionSource, "software_catalog", "The software entry decides before the kind entry");
const off = resolve([kindEntry, switchedOff]);
assert.equal(off?.protectionLevel, 0, "A user entry at 0 decides even though the kind default protects");
assert.equal(off?.defaultProtectionSource, "software_catalog");
assert.equal(resolve([processEntry]), null, "Process entries need a process and do not decide for software detail");
assert.equal(resolve([{ ...catalogEntry, state: "missing" }, kindEntry])?.protectionSource, "system_default",
  "Only active entries take part");
assert.equal(resolve([entry({ id: "software:WINDOWS-SERVICE:AUDIOSRV", softwareId: softwareId.toUpperCase(), protectionSource: "report" })])
  ?.protectionSource, "report", "Software ids compare case-insensitively");
// 同一层有几个条目：等级高的决定；等级相同取编号序数较小的。
assert.equal(resolve([entry({ id: "b", protectionLevel: 0 }), entry({ id: "c", protectionSource: "report" })])?.id, "c");
assert.equal(resolve([entry({ id: "b", protectionSource: "report" }), entry({ id: "a" })])?.id, "a");
// 不认识的来源照样决定，交给界面原样显示。
assert.equal(resolve([entry({ id: "future", protectionSource: "future_source" })])?.protectionSource, "future_source");

function entry(overrides: Record<string, unknown>) {
  return {
    id: "software:windows-service:audiosrv", targetType: "software", targetKey: `software:${softwareId}`,
    softwareId, softwareName: "Windows Audio", softwareKind: "windows_service",
    protectedAt: "2026-10-29T08:00:00.000Z", protectionSource: "software_detail", createdFromReportId: "software-detail",
    lastVerifiedAt: null, state: "active", allowsPlacementAvoidance: true, protectionLevel: 1,
    defaultProtectionLevel: null, defaultProtectionSource: null, policyGroupId: null, processName: null, executablePath: null,
    ...overrides
  };
}
