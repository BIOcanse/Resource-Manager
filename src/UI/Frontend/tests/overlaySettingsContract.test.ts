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
const metricIds = ["target.fps", "target.frameTime", "target.onePercentLow",
  "target.pointOnePercentLow", "target.cpu", "target.gpu", "target.memory", "target.vram"];

test("overlay settings and target metrics have text in every supported copy", () => {
  for (const copy of copies) {
    for (const value of Object.values(copy.performanceOverlay)) {
      assert.equal(typeof value, "string");
      assert.ok(value.trim());
    }
    for (const id of metricIds) {
      assert.ok(copy.metricLabel[id as keyof typeof copy.metricLabel]);
    }
  }
});

test("software detail uses the shared metric picker as a multi select", () => {
  const detail = readFileSync(new URL("../src/components/SoftwareDetailModal.tsx", import.meta.url), "utf8");
  const overlay = readFileSync(new URL("../src/features/overlay/SoftwareOverlaySettings.tsx", import.meta.url), "utf8");
  assert.match(detail, /<TabsPanel value="overlay"/);
  assert.match(overlay, /<MetricModal[^>]*multiSelect/s);
  assert.match(overlay, /performanceOverlay\.injectionWarning/);
  assert.match(overlay, /savePerformanceOverlaySettings\(current\)/);
});
