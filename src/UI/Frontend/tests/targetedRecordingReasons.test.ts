import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { zhAppCopy } from "../src/i18n/copy/zh/index.ts";
import { enAppCopy } from "../src/i18n/copy/en/index.ts";
import { zhAppCopy as zhTwAppCopy } from "../src/i18n/copy/zh-TW/index.ts";
import ja from "../src/i18n/copy/locales/ja-JP.ts";
import ko from "../src/i18n/copy/locales/ko-KR.ts";
import fr from "../src/i18n/copy/locales/fr-FR.ts";
import de from "../src/i18n/copy/locales/de-DE.ts";
import es from "../src/i18n/copy/locales/es-ES.ts";
import ru from "../src/i18n/copy/locales/ru-RU.ts";
import { itemCatalogDecoder } from "../src/features/targetedReport/targetedRecordingApi.ts";

// 录制缺口和浮层项的具体原因（docs/targeted_recording_views_2026-10-30.md §3.5、§3.9）：后端常量直接从
// TargetedRecordingModels.cs 读，九种语言都要有文字；录制自己的五个原因用报告文案（reportInfo.gapReasons），
// 其他原因（源和读取方给的）用浮层的原因表，原生壳浮层也用那份表。
const models = readFileSync(new URL("../../../Core/Infrastructure/Monitoring/TargetedRecording/TargetedRecordingModels.cs",
  import.meta.url), "utf8");

function constants(className: string): string[] {
  const start = models.indexOf(`public static class ${className}`);
  assert.ok(start >= 0, `${className} is in TargetedRecordingModels.cs`);
  const body = models.slice(start, models.indexOf("\n}", start));
  return [...body.matchAll(/public const string \w+ = "([^"]+)";/g)].map((match) => match[1]);
}

const recordingOwn = ["item-unavailable", "value-unavailable", "size-limit", "frames-incomplete", "frames-window-skipped"];
const gapReasons = constants("TargetedRecordingGapReasons");
const readReasons = constants("TargetedRecordingReadReasons");
assert.deepEqual(gapReasons.filter((reason) => recordingOwn.includes(reason)).sort(), [...recordingOwn].sort());
for (const reason of ["not-applicable", "no-source", "source-not-refreshed", "target-gpu-unresolved"]) {
  assert.ok(gapReasons.includes(reason), `${reason} is a backend gap reason`);
}
// The concrete source code the 10-31 recording showed (resource breakdown, gpu.N.vram without dedicated memory).
const sourceCodes = ["gpu-dedicated-memory-metrics-unsupported"];
const labelled = [...gapReasons.filter((reason) => !recordingOwn.includes(reason)), ...readReasons, ...sourceCodes];

const copies = { "zh-CN": zhAppCopy, "zh-TW": zhTwAppCopy, "en-US": enAppCopy, "ja-JP": ja, "ko-KR": ko, "fr-FR": fr,
  "de-DE": de, "es-ES": es, "ru-RU": ru } as const;
for (const [language, copy] of Object.entries(copies)) {
  const reasons = copy.performanceOverlay.itemReasons as Record<string, string | undefined>;
  for (const reason of labelled) {
    const text = reasons[reason];
    assert.ok(typeof text === "string" && text.trim().length > 0 && text !== reason, `${language} labels ${reason}`);
  }
  const own = copy.targetedReport.reportInfo.gapReasons;
  for (const key of ["itemUnavailable", "valueUnavailable", "sizeLimit", "framesIncomplete", "framesWindowSkipped"] as const) {
    assert.ok(own[key].trim().length > 0, `${language} labels ${key}`);
  }
  // The target GPU items have names in the picker, the report and the overlay.
  const metricLabel = copy.metricLabel as Record<string, unknown>;
  for (const id of ["gpu.target.usage", "gpu.target.temperature", "gpu.target.vram"]) {
    assert.ok(typeof metricLabel[id] === "string" && (metricLabel[id] as string).trim().length > 0, `${language} names ${id}`);
  }
}
// Simplified and traditional Chinese are written separately.
assert.notEqual(zhAppCopy.performanceOverlay.itemReasons["source-not-refreshed"], zhTwAppCopy.performanceOverlay.itemReasons["source-not-refreshed"]);

// The catalog carries the not-applicable / no-source reason; an older backend without the field still decodes.
const entry = { kind: "metric", id: "gpu.0.vram", unit: "B", sources: [], naturalIntervalMs: null, uses: ["recording", "overlay"] };
assert.equal(itemCatalogDecoder.decode([{ ...entry, unavailableReason: "not-applicable" }])[0].unavailableReason, "not-applicable");
assert.equal(itemCatalogDecoder.decode([{ ...entry, unavailableReason: null }])[0].unavailableReason, null);
assert.equal(itemCatalogDecoder.decode([entry])[0].unavailableReason, null);
assert.throws(() => itemCatalogDecoder.decode([{ ...entry, unavailableReason: 4 }]));
assert.throws(() => itemCatalogDecoder.decode([{ ...entry, unavailableReason: "" }]));

console.log("targeted recording reasons: ok");
