import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { controlCapabilityLabel, controlDisplayText } from "../src/features/control/controlPresentation.ts";
import { createAppCopy } from "../src/i18n/copy/index.ts";
import { applyLanguage, currentLanguage } from "../src/text.ts";

const en = createAppCopy("en-US").control.presentation;
const zh = createAppCopy("zh-CN").control.presentation;
const catalog = readFileSync(new URL("../../../../src/Core/Infrastructure/Control/WindowsControlObjectCatalog.cs", import.meta.url), "utf8");
const ids = new Set([...catalog.matchAll(/"((?:cpu|gpu|fan)\.[a-z-]+)"/g)].map(match => match[1]));
ids.delete("cpu.fanRpm");
for (const id of ids) {
  const capability = { id, label: "中文标签" };
  assert.doesNotMatch(controlCapabilityLabel(capability, en), /\p{Script=Han}/u, id);
  assert.equal(controlCapabilityLabel(capability, zh), zh.labels[id], id);
}
assert.equal(controlCapabilityLabel({ id: "cpu.power-limit", label: "持续功耗上限（PL1）" }, en), "Sustained power limit (PL1)");
assert.equal(controlCapabilityLabel({ id: "extension.custom", label: "Vendor custom control" }, en), "Vendor custom control");
assert.equal(controlDisplayText("风扇2", en), "Fan 2");
assert.equal(controlDisplayText("档", en), "steps");
assert.equal(controlDisplayText("My custom fan", en), "My custom fan");
assert.equal(controlDisplayText("只读，写入未接入。", en), "Read-only; write support is not implemented.");

const sameCapability = { id: "fan.curve", label: "转速曲线" };
for (const language of ["en-US", "zh-CN", "en-US"]) {
  applyLanguage(language);
  for (let attempt = 0; currentLanguage() !== language && attempt < 100; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), language);
  assert.equal(controlCapabilityLabel(sameCapability), language === "en-US" ? "Fan speed curve" : "转速曲线");
}
console.log(`English control presentation: ${ids.size} capability IDs, units, reasons, names and Chinese/English switching passed. All selectable languages are checked by test:localization-coverage.`);
