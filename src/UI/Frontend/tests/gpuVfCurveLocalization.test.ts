import assert from "node:assert/strict";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { gpuVfCurveCopy } from "../src/i18n/copy/gpuVfCurve.ts";
import { gpuVfCurveReasons } from "../src/i18n/copy/gpuVfCurveReasons.ts";
import { gpuVfCurveReasonText } from "../src/presentation/gpuVfCurveReasonText.ts";
const codes = ["gpu-vf-curve-object-not-gpu", "gpu-vf-curve-binding-mismatch", "gpu-vf-curve-common-delta-out-of-range", "gpu-vf-curve-in-use",
  "gpu-vf-curve-preset-not-found", "gpu-vf-curve-name-invalid", "gpu-vf-curve-default-unavailable", "gpu-vf-curve-command-invalid",
  "gpu-vf-curve-legacy-conflict", "nvidia-vf-restore-protocol-unverified", "nvidia-vf-get-info-failed:-3"];
for (const language of languageOptions.map(option => option.id).filter(id => id !== "system")) {
  const copy = await loadAppCopy(language);
  assert.deepEqual(copy.gpuVfCurve, gpuVfCurveCopy[language], `${language} loads its own complete production GPU curve copy`);
  for (const text of Object.values(copy.gpuVfCurve)) assert.ok(text.trim().length);
  for (const text of Object.values(gpuVfCurveReasons[language])) assert.ok(text.trim().length);
  for (const code of codes) assert.ok(gpuVfCurveReasonText({ code }, language)?.includes(code), "Exact backend failure remains inspectable");
  if (language !== "en-US") assert.notEqual(copy.gpuVfCurve.title, gpuVfCurveCopy["en-US"].title);
}
assert.notEqual(gpuVfCurveCopy["zh-CN"].save, gpuVfCurveCopy["zh-TW"].save, "Simplified and traditional Chinese use their own copy");
assert.ok(gpuVfCurveReasonText({ code: "gpu-vf-curve-common-delta-out-of-range", arguments: { minimum: -15000, maximum: 30000, unit: "kHz" } }, "en-US")?.includes("[-15000, 30000] kHz"));
console.log("GPU curve editor and backend reasons cover all supported languages");
