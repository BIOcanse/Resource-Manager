import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { zhAppCopy } from "../src/i18n/copy/zh/index.ts";
import { zhAppCopy as zhTwAppCopy } from "../src/i18n/copy/zh-TW/index.ts";
import { enAppCopy } from "../src/i18n/copy/en/index.ts";
import ja from "../src/i18n/copy/locales/ja-JP.ts";
import ko from "../src/i18n/copy/locales/ko-KR.ts";
import fr from "../src/i18n/copy/locales/fr-FR.ts";
import de from "../src/i18n/copy/locales/de-DE.ts";
import es from "../src/i18n/copy/locales/es-ES.ts";
import ru from "../src/i18n/copy/locales/ru-RU.ts";

const copies = [zhAppCopy, zhTwAppCopy, enAppCopy, ja, ko, fr, de, es, ru];
// 帧项沿用的名称（frames.fps、frames.present、frames.low1、frames.low01）。
const frameLabelIds = ["target.fps", "target.frameTime", "target.onePercentLow", "target.pointOnePercentLow"];
const reasons = ["target-not-running", "frame-events-lost", "no-frame-data", "metric-unavailable",
  "process-cpu-unavailable", "process-metric-unavailable"];
const issues = ["too-many", "duplicate", "kind", "precision", "interval", "frames-interval", "id", "source"];

test("overlay settings, item names and reasons have text in every supported copy", () => {
  for (const copy of copies) {
    // 文案是字符串、带参数的函数或嵌套的表：用显式队列逐层检查，每个字符串和函数的结果都不能为空。
    const queue: unknown[] = [copy.performanceOverlay];
    let visited = 0;
    while (queue.length > 0 && visited < 1000) {
      const value = queue.shift();
      visited += 1;
      if (typeof value === "string") assert.ok(value.trim());
      else if (typeof value === "function") assert.ok(String(value("a", "b", "c")).trim());
      else if (value && typeof value === "object") queue.push(...Object.values(value));
      else assert.fail(`unexpected copy value ${String(value)}`);
    }
    assert.equal(queue.length, 0);
    for (const id of frameLabelIds) {
      assert.ok(copy.metricLabel[id as keyof typeof copy.metricLabel]);
    }
    for (const reason of reasons) {
      assert.ok(copy.performanceOverlay.itemReasons[reason as keyof typeof copy.performanceOverlay.itemReasons]);
    }
    for (const issue of issues) {
      assert.ok(copy.performanceOverlay.itemsInvalid[issue as keyof typeof copy.performanceOverlay.itemsInvalid]);
    }
    assert.ok(copy.performanceOverlay.itemSoftware("CPU").includes("CPU"));
  }
});

test("software detail uses the shared item picker in the overlay context", () => {
  const detail = readFileSync(new URL("../src/components/SoftwareDetailModal.tsx", import.meta.url), "utf8");
  const overlay = readFileSync(new URL("../src/features/overlay/SoftwareOverlaySettings.tsx", import.meta.url), "utf8");
  assert.match(detail, /<TabsPanel value="overlay"/);
  assert.match(overlay, /<ItemPicker[^>]*use="overlay"[^>]*maximumItems=\{constraints\(\)\.maximumItems\}/s);
  assert.match(overlay, /moveView\(current\(\)\.items, index\(\), -1\)/);
  assert.match(overlay, /moveView\(current\(\)\.items, index\(\), 1\)/);
  assert.match(overlay, /performanceOverlay\.injectionWarning/);
  assert.match(overlay, /savePerformanceOverlaySettings\(runtime\.requestClient, current, before\)/);
});


test("overlay editors and the picker cannot mutate the draft during a save", () => {
  const overlay = readFileSync(new URL("../src/features/overlay/SoftwareOverlaySettings.tsx", import.meta.url), "utf8");
  for (const control of overlay.matchAll(/<(input|StandardSelect)\b[^>]*>/gs)) {
    assert.match(control[0], /disabled=\{saving\(\)\}/, control[0]);
  }
  for (const button of overlay.matchAll(/<button\b[^>]*>/gs)) {
    assert.match(button[0], /disabled=\{[^}]*saving\(\)/, button[0]);
  }
  assert.match(overlay, /current && !saving\(\) \?/);
  assert.match(overlay, /if \(!current \|\| !before \|\| saving\(\)\) return/);
  assert.match(overlay, /open=\{pickerOpen\(\) && !saving\(\)\}/);
  // 项不合规时不能保存。
  assert.match(overlay, /itemsIssue\(\) !== null \|\| saving\(\)/);
});
