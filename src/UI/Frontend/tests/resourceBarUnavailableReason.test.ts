import assert from "node:assert/strict";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { uiText } from "../src/text.ts";
import {
  resourceBarHardwareSamplePrefix,
  resourceBarUnavailableReasonFamilies,
  resourceBarUnavailableText,
  type ResourceBarUnavailableFamily
} from "../src/features/resourceBreakdown/resourceBarUnavailableReason.ts";

// 资源条无读数的原因：前端的对应表里每个代码都归到一类，每一类（含带参数的 hardware-sample-* 家族）九种语言都有正式的一句。
// 只用仓库里提交的文件，哪里跑结果都一样。对应表和后端权威列表是否完全一致，由开发工作副本里的
// tests/resourceBarReasonCodesAuthority.test.ts（npm run test:reason-codes-authority）核对，不在这里。

const families: readonly ResourceBarUnavailableFamily[] = [
  "warming", "sourceUnavailable", "incomplete", "unsupported", "totalUnavailable",
  "missing", "stale", "failed", "invalid", "attributionUnavailable"
];

// 1. 每个代码都归到已知的一类，每一类都有代码用到。
for (const [code, family] of Object.entries(resourceBarUnavailableReasonFamilies)) {
  assert.ok(families.includes(family), `${code}: maps to a known family (${family})`);
  assert.ok(!code.startsWith(resourceBarHardwareSamplePrefix), `${code}: the hardware-sample family is not a literal code`);
}
assert.deepEqual([...new Set(Object.values(resourceBarUnavailableReasonFamilies))].sort(), [...families].sort(), "every family is used");

// 2. 九种语言：每一类非空、同一语言里各类不重复、非英语的不照抄英语；家族那句带上异常类型名。
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
  const sample = copy.sampleException("<Type>");
  assert.ok(sample.includes("<Type>") && sample.trim().length > "<Type>".length, `${language}: the sampling-error sentence shows the type name`);
  if (language !== "en-US") assert.notEqual(sample, english.sampleException("<Type>"), `${language}: sampling-error sentence translated`);
}

// 3. 显示：认识的代码给这一类的话；家族把后缀原样写进句子；不认识的代码（包括只有前缀的）原样显示。
assert.equal(resourceBarUnavailableText("hardware-source-unavailable"), uiText.resourceBreakdown.unavailableReason.sourceUnavailable);
assert.equal(resourceBarUnavailableText("system-total-unavailable"), uiText.resourceBreakdown.unavailableReason.totalUnavailable);
assert.equal(resourceBarUnavailableText("hardware-sample-InvalidOperationException"),
  uiText.resourceBreakdown.unavailableReason.sampleException("InvalidOperationException"));
assert.equal(resourceBarUnavailableText("hardware-sample-"), "hardware-sample-");
assert.equal(resourceBarUnavailableText("some-future-reason"), "some-future-reason");
assert.equal(resourceBarUnavailableText("constructor"), "constructor");

console.log(`resourceBarUnavailableReason: ${Object.keys(resourceBarUnavailableReasonFamilies).length} codes and the hardware-sample family, ${families.length} families, ${languages.length} languages ok`);
