import assert from "node:assert/strict";
import { machineEvidenceLines, machineFormName } from "../src/features/deviceTopology/machineFormPresentation.ts";
import { applyLanguage } from "../src/text.ts";
import type { DeviceTopologyMachineIdentity } from "../src/types.ts";
const fact = <T>(value: T | null) => ({ value, reason: value === null ? "source-unavailable" : null });
function sample(formFactor: DeviceTopologyMachineIdentity["formFactor"], chassis: number[] | null = [10], battery: boolean | null = true): DeviceTopologyMachineIdentity {
  return { formFactor, evidence: { chassisTypes: fact(chassis), systemBatteryPresent: fact(battery), cpuModels: fact([]), gpuModels: fact([]), gpuAttachments: fact([]), workstationComponents: [] } };
}
await applyLanguage("zh-CN");
const mobile = sample("mobile_workstation");
mobile.evidence.workstationComponents = [{ component: "gpu", model: "RTX PRO 5000", ruleId: "professional-gpu" }];
assert.equal(machineFormName(mobile), "移动工作站");
assert.deepEqual(machineEvidenceLines(mobile), [
  { label: "机箱类型", value: "笔记本" }, { label: "有内置电池", value: null }, { label: "专业 GPU", value: "RTX PRO 5000" }
]);
const raw = sample("workstation", [33, 250], false);
raw.evidence.workstationComponents = [{ component: "cpu", model: "Xeon", ruleId: "future-rule" }];
assert.deepEqual(machineEvidenceLines(raw), [
  { label: "机箱类型", value: "33, 250" }, { label: "无内置电池", value: null }, { label: "future-rule", value: "Xeon" }
]);
assert.deepEqual(machineEvidenceLines(sample("unknown", null, null)), [
  { label: "机箱类型", value: "未知 (source-unavailable)" }, { label: "电池", value: "未知 (source-unavailable)" }
]);
assert.equal(machineEvidenceLines(sample("unknown", [], null))[0].value, "[]", "empty is not unknown");
const forms = ["laptop", "mobile_workstation", "workstation", "desktop", "unknown"] as const;
for (const language of ["en-US", "zh-CN", "zh-TW", "de-DE", "es-ES", "fr-FR", "ja-JP", "ko-KR", "ru-RU"] as const) {
  await applyLanguage(language);
  const names = forms.map((form) => machineFormName(sample(form)));
  assert.equal(new Set(names).size, 5, language);
  assert.ok(machineEvidenceLines(sample("unknown", null, null)).every((line) => line.value?.includes("source-unavailable")), language);
}
await applyLanguage("zh-CN");
// 整机栏的识别信息每样只说一遍（用户「不要重复一遍」，协调者 10-26 走查 MECHREVO 出现三次）。
{
  const { machineIdentity } = await import("../src/features/deviceTopology/machineIdentityPresentation.ts");
  const system = (patch: Record<string, unknown>) => ({
    manufacturer: "MECHREVO", model: "JIAOLONG Series", brandDisplayName: "MECHREVO DRR / JIAOLONG Series", brandLogoText: "MECHREVO",
    biosVersion: "N.1.18MRO15", baseBoardManufacturer: "MECHREVO", baseBoardProduct: "JIAOLONG Series-X6DR55xx-B2", ...patch
  });
  const chips = (identity: ReturnType<typeof machineIdentity>) => identity.chips.map((chip) => `${chip.label ?? ""}|${chip.value}|${chip.title}`);

  // 真机：标志字已写 MECHREVO，厂商不再写；主板厂商不列；主板型号只列系列后面的部分，完整原文在提示里。
  const real = machineIdentity(system({}));
  assert.equal(real.wordmark, "MECHREVO");
  assert.equal(real.vendor, null);
  assert.equal(real.series, "JIAOLONG Series");
  assert.deepEqual(chips(real), ["主板型号|X6DR55xx-B2|JIAOLONG Series-X6DR55xx-B2", "BIOS|N.1.18MRO15|N.1.18MRO15"]);
  const shownTexts = [real.wordmark, real.vendor, real.series, ...real.chips.map((chip) => chip.value)].filter(Boolean) as string[];
  assert.equal(shownTexts.filter((text) => /MECHREVO/i.test(text)).length, 1, "the vendor is shown once");
  assert.equal(shownTexts.filter((text) => /JIAOLONG/i.test(text)).length, 1, "the series is shown once");

  // 大小写不同也算同一个名字；和系列完全相同的主板型号不列。
  const sameProduct = machineIdentity(system({ manufacturer: "Mechrevo", baseBoardManufacturer: "mechrevo ", baseBoardProduct: "jiaolong series" }));
  assert.equal(sameProduct.vendor, null);
  assert.deepEqual(chips(sameProduct), ["BIOS|N.1.18MRO15|N.1.18MRO15"]);

  // 标志字是子品牌时厂商照写；装机主板（厂商、型号都不同）整条列出并写明是主板。
  const builtPc = machineIdentity(system({ manufacturer: "ASUSTeK COMPUTER INC.", brandLogoText: "ROG", model: "Z",
    baseBoardManufacturer: "ASUSTeK COMPUTER INC.", baseBoardProduct: "Z790-A" }));
  assert.equal(builtPc.vendor, "ASUSTeK COMPUTER INC.");
  // “Z”后面不是分隔符：不把“Z790-A”截成“790-A”。
  assert.deepEqual(chips(builtPc), ["主板型号|Z790-A|Z790-A", "BIOS|N.1.18MRO15|N.1.18MRO15"]);
  const otherBoard = machineIdentity(system({ baseBoardManufacturer: "Micro-Star International", baseBoardProduct: "MS-7E12" }));
  assert.deepEqual(chips(otherBoard), ["主板厂商|Micro-Star International|Micro-Star International", "主板型号|MS-7E12|MS-7E12", "BIOS|N.1.18MRO15|N.1.18MRO15"]);

  // 后端没发的项仍写“未知”。
  const missing = machineIdentity(system({ biosVersion: null, baseBoardManufacturer: null, baseBoardProduct: null }));
  assert.deepEqual(chips(missing), ["|主板厂商未知|主板厂商未知", "|主板型号未知|主板型号未知", "|BIOS 未知|BIOS 未知"]);

  // 九种语言都有标签名。
  for (const language of ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const) {
    await applyLanguage(language);
    const labels = machineIdentity(system({ baseBoardManufacturer: "MSI", baseBoardProduct: "MS-7E12" })).chips.map((chip) => chip.label);
    assert.ok(labels.length === 3 && labels.every((label) => typeof label === "string" && label.length > 0), `${language} labels the chips`);
  }
  await applyLanguage("zh-CN");
}
