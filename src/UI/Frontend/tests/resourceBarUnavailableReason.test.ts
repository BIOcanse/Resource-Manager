import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { uiText } from "../src/text.ts";
import {
  resourceBarUnavailableReasonFamilies,
  resourceBarUnavailableText,
  type ResourceBarUnavailableFamily
} from "../src/features/resourceBreakdown/resourceBarUnavailableReason.ts";

// 资源条无读数的原因（b14fc21a 的显式无读数合同）：后端会发的每个代码都有对应的一类，每一类九种语言都有正式的一句。

// 1. 后端会发的代码就是这些：从发出或透传这些代码的后端源文件里取出来，和前端的对应表逐个相等（新增代码要先补文字）。
const repository = new URL("../../../../", import.meta.url);
const backendSources = [
  "src/Core/Endpoints/Transport/ResourceBreakdownCurrentBars.cs",
  "src/Core/Infrastructure/ResourceBreakdown/WindowsResourceBreakdownSampler.Bars.cs",
  "src/Core/Infrastructure/ResourceBreakdown/WindowsResourceBreakdownSampler.Bars.Throughput.cs",
  "src/Core/Infrastructure/ResourceBreakdown/WindowsResourceBreakdownSampler.GpuAllocations.cs",
  "src/Core/Infrastructure/ResourceBreakdown/ResourceBreakdownSnapshotState.cs",
  "src/ReadWriteCore/Native/Snapshots/NativeMetricSnapshotCommittedProjection.cs",
  "src/ReadWriteCore/Windows/Metrics/Current/LastSuccessfulHardwareMetricSnapshot.cs"
];
const emitted = new Set<string>();
for (const source of backendSources) {
  const text = readFileSync(new URL(source, repository), "utf8");
  for (const match of text.matchAll(/"((?:resource-breakdown|disk-io|hardware|gpu-dedicated-memory)-[a-z0-9-]+)"/g)) emitted.add(match[1]);
}
assert.ok(emitted.size >= 20, `found the backend reason codes (${emitted.size})`);
assert.deepEqual([...emitted].sort(), Object.keys(resourceBarUnavailableReasonFamilies).sort(),
  "every reason code the backend emits has a family, and the table lists no code the backend does not emit");

// 2. 每一类九种语言都有一句：非空、同一语言里各类不重复、非英语的不照抄英语。
const families: readonly ResourceBarUnavailableFamily[] = [
  "warming", "sourceUnavailable", "incomplete", "unsupported", "totalUnavailable",
  "missing", "stale", "failed", "invalid", "attributionUnavailable"
];
assert.deepEqual([...new Set(Object.values(resourceBarUnavailableReasonFamilies))].sort(), [...families].sort(), "every family is used");
const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const;
const english = (await loadAppCopy("en-US")).resourceBreakdown.unavailableReason;
for (const language of languages) {
  const copy = (await loadAppCopy(language)).resourceBreakdown.unavailableReason;
  for (const family of families) {
    assert.equal(typeof copy[family], "string", `${language}: ${family} present`);
    assert.ok(copy[family].trim().length > 0, `${language}: ${family} not empty`);
    if (language !== "en-US") assert.notEqual(copy[family], english[family], `${language}: ${family} translated`);
  }
  assert.equal(new Set(families.map((family) => copy[family])).size, families.length, `${language}: each family reads differently`);
}

// 3. 显示：认识的代码给这一类的话，不认识的代码原样显示。
assert.equal(resourceBarUnavailableText("hardware-source-unavailable"), uiText.resourceBreakdown.unavailableReason.sourceUnavailable);
assert.equal(resourceBarUnavailableText("disk-io-window-incomplete"), uiText.resourceBreakdown.unavailableReason.incomplete);
assert.equal(resourceBarUnavailableText("some-future-reason"), "some-future-reason");
assert.equal(resourceBarUnavailableText("constructor"), "constructor");

console.log(`resourceBarUnavailableReason: ${emitted.size} codes, ${families.length} families, ${languages.length} languages ok`);
