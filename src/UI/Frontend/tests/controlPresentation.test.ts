import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { controlCapabilityLabel, controlDisplayText } from "../src/control/controlPresentation.ts";
import { applyLanguage, currentLanguage } from "../src/text.ts";

const catalog = readFileSync(new URL("../../../../Resource Manager/Resource Manager-APP/Infrastructure/Control/WindowsControlObjectCatalog.cs", import.meta.url), "utf8");
const ids = new Set([...catalog.matchAll(/"((?:cpu|gpu|fan)\.[a-z-]+)"/g)].map(match => match[1]));
ids.delete("cpu.fanRpm");
for (const id of ids) {
  const capability = { id, label: "中文标签" };
  assert.doesNotMatch(controlCapabilityLabel(capability, "en-US"), /\p{Script=Han}/u, id);
  assert.equal(controlCapabilityLabel(capability, "zh-CN"), capability.label);
}
assert.equal(controlCapabilityLabel({ id: "cpu.power-limit", label: "持续功耗上限（PL1）" }, "en-US"), "Sustained power limit (PL1)");
assert.equal(controlCapabilityLabel({ id: "extension.custom", label: "Vendor custom control" }, "en-US"), "Vendor custom control");
assert.equal(controlDisplayText("风扇2", "en-US"), "Fan 2");
assert.equal(controlDisplayText("档", "en-US"), "steps");
assert.equal(controlDisplayText("My custom fan", "en-US"), "My custom fan");
assert.equal(controlDisplayText("只读，写入未接入。", "en-US"), "Read-only; write support is not implemented.");

const sameCapability = { id: "fan.curve", label: "转速曲线" };
for (const language of ["en-US", "zh-CN", "en-US"]) {
  applyLanguage(language);
  for (let attempt = 0; currentLanguage() !== language && attempt < 100; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), language);
  assert.equal(controlCapabilityLabel(sameCapability), language === "en-US" ? "Fan speed curve" : "转速曲线");
}
console.log(`Control localization: ${ids.size} capability IDs, units, reasons, names and language switching passed.`);
