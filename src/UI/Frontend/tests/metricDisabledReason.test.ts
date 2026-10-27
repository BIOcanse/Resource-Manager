import assert from "node:assert/strict";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { applyLanguage } from "../src/i18n/appTextStore.ts";
import { backendMessageCodeText } from "../src/presentation/backendMessage.ts";
import { metricDisabledReasonCode, userFacingMetricUnavailableReason } from "../src/presentation/userFacingText.ts";

// 指标选择器里不可选的指标：写后端 disabledReason（{domain, code, args}）对应的那句话，原样代码在提示里；
// 认不出的域或码写原样代码，不换成通用的「状态已更新」之类的说法。真实切片 20261027T043049Z 里 gpu.1.* 都是 {3, 3, []}。
assert.equal(backendMessageCodeText({ domain: 3, code: 3, args: [] }), "3/3");
assert.equal(backendMessageCodeText({ domain: 3, code: 2, args: ["LibreHardwareMonitor"] }), "3/2 [LibreHardwareMonitor]");
assert.equal(backendMessageCodeText({ domain: 3, code: 9, args: ["a", "b"] }), "3/9 [a, b]");

const languages = languageOptions.filter(option => option.id !== "system");
assert.equal(languages.length, 9);
const warn = console.warn;
console.warn = () => undefined;
const seen = new Map<string, string>();
try {
  for (const { id } of languages) {
    const copy = await loadAppCopy(id);
    await applyLanguage(id);
    const metric = copy.backendMessage.metric;
    assert.equal(userFacingMetricUnavailableReason(null, { domain: 3, code: 3, args: [] }), metric.noValidReading, id);
    assert.equal(userFacingMetricUnavailableReason(null, { domain: 3, code: 1, args: [] }), metric.notExposed, id);
    assert.equal(userFacingMetricUnavailableReason("LibreHardwareMonitor", { domain: 3, code: 2, args: [] }),
      metric.needsComponent("LibreHardwareMonitor"), id);
    for (const unknown of [{ domain: 3, code: 9, args: [] }, { domain: 99, code: 1, args: ["x"] }]) {
      const text = userFacingMetricUnavailableReason(null, unknown);
      assert.equal(text, copy.backendMessage.reasonCode(backendMessageCodeText(unknown)), `${id}: an unknown reason shows its raw code`);
      assert.notEqual(text, copy.backendMessage.unknown, id);
    }
    assert.equal(metricDisabledReasonCode({ domain: 3, code: 3, args: [] }), copy.backendMessage.reasonCode("3/3"), id);
    assert.ok(copy.backendMessage.reasonCode("3/3").includes("3/3") && copy.backendMessage.reasonCode("3/3") !== "3/3", id);
    assert.ok(metric.noValidReading.trim() && metric.noValidReading !== copy.dashboard.noReading, `${id}: code 3 says no valid reading was returned`);
    if (!["zh-CN", "zh-TW", "ja-JP"].includes(id)) assert.ok(!/[㐀-鿿]/.test(metric.noValidReading), `${id}: base-language prose`);
    assert.ok(!seen.has(metric.noValidReading), `${id}: copied from ${seen.get(metric.noValidReading)}`);
    seen.set(metric.noValidReading, id);
  }
} finally {
  console.warn = warn;
  await applyLanguage("zh-CN");
}
console.log("Metric picker disabled reasons: backend text, raw domain/code/args on hover, raw code for unknown reasons, in nine languages passed");
