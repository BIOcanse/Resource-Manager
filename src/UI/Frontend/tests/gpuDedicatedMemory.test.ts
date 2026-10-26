import assert from "node:assert/strict";
import {
  dedicatedMemoryEmptyLabel,
  dedicatedMemoryHint,
  dedicatedMemoryState,
  gpuFullClassification
} from "../src/features/gpuScheduling/gpuDedicatedMemory.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";

/*
 * GPU 调度模型上专用显存指标能力的三种状态（后端 hasDedicatedMemoryMetrics：true／false／null）。
 * 三种状态在每种界面语言下都有各自的正式说明，空值不推断成有或没有。
 */

assert.equal(dedicatedMemoryState(true), "supported");
assert.equal(dedicatedMemoryState(false), "unsupported");
assert.equal(dedicatedMemoryState(null), "unknown");

// 「已满」判断：阈值 GPU 90%、显存 80%。
const classify = (usage: number | null, vram: number | null, capability: boolean | null) =>
  gpuFullClassification(usage, vram, 90, 80, capability);
// 有专用显存：显存到阈值算满；两项都有读数且都没到才算未满；显存没读数就无从判断。
assert.deepEqual(classify(10, 85, true), { full: true, reason: uiText.gpuScheduling.vramFull });
assert.deepEqual(classify(10, 50, true), { full: false, reason: null });
assert.deepEqual(classify(10, null, true), { full: null, reason: null });
// 没有专用显存：只看 GPU 占用，显存读数不参与。
assert.deepEqual(classify(10, 85, false), { full: false, reason: null });
assert.deepEqual(classify(10, null, false), { full: false, reason: null });
// 不知道：显存一项既不算满也不算未满，只有 GPU 占用到阈值才判满。
assert.deepEqual(classify(10, 85, null), { full: null, reason: null });
assert.deepEqual(classify(10, 50, null), { full: null, reason: null });
assert.deepEqual(classify(95, 85, null), { full: true, reason: uiText.gpuScheduling.gpuFull });
// GPU 占用和显存都满时两条原因都列出。
assert.deepEqual(classify(95, 85, true),
  { full: true, reason: `${uiText.gpuScheduling.gpuFull} / ${uiText.gpuScheduling.vramFull}` });

const seen = new Map<string, string[]>();
for (const language of languageOptions) {
  if (language.id === "system") continue;
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  applyLanguage(language.id);
  for (let attempt = 0; attempt < 100 && currentLanguage() !== language.id; attempt++)
    await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(currentLanguage(), language.id);
  const text = uiText.gpuScheduling;
  // 显存条上的字：有专用显存时没读数写「暂无数据」，没有写「共享」，不知道另写一个词。
  assert.equal(dedicatedMemoryEmptyLabel(true), text.noData, language.id);
  assert.equal(dedicatedMemoryEmptyLabel(false), text.shared, language.id);
  assert.equal(dedicatedMemoryEmptyLabel(null), text.dedicatedMemoryUnknown, language.id);
  const labels = [true, false, null].map((value) => dedicatedMemoryEmptyLabel(value));
  assert.equal(new Set(labels).size, 3, `${language.id}: the three states read alike: ${labels.join(" | ")}`);
  const hints = [true, false, null].map((value) => dedicatedMemoryHint(value));
  assert.equal(new Set(hints).size, 3, `${language.id}: the three hints read alike`);
  for (const hint of [...hints, text.dedicatedMemoryUnknown]) {
    assert(hint.trim().length > 0, `${language.id}: empty copy`);
    assert.equal(hint, hint.trim(), `${language.id}: copy has stray spaces`);
  }
  // 中文以外的语言不出现中文。
  if (!language.id.startsWith("zh")) {
    for (const hint of [...hints, text.dedicatedMemoryUnknown])
      assert(!/[一-鿿]/u.test(hint) || language.id === "ja-JP", `${language.id}: untranslated copy ${hint}`);
  }
  seen.set(language.id, [...hints, text.dedicatedMemoryUnknown]);
}
assert.equal(seen.size, 9, `expected nine interface languages, got ${[...seen.keys()].join(", ")}`);
// 每种语言的说明各不相同（没有直接照抄英文）。
const english = seen.get("en-US")!;
for (const [language, copy] of seen) {
  if (language !== "en-US") assert.notDeepEqual(copy, english, `${language} copies the English text`);
}

console.log("gpu dedicated memory tests passed");
