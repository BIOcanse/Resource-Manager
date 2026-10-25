import assert from "node:assert/strict";
import { formatGpuProviderCapability, formatGpuLaunchOutcome, gpuPlacementReason } from "../src/components/gpuPlacementPresentation.ts";
import { startupTargetGpuOptionsForSoftwarePolicy } from "../src/components/gpuPlacementTargetOptions.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";

for (const language of languageOptions) {
  if (language.id === "system") continue;
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  applyLanguage(language.id);
  for (let attempt = 0; attempt < 100 && currentLanguage() !== language.id; attempt++)
    await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(currentLanguage(), language.id);
  const reasons = uiText.softwareDetail.providerPhase.reasons;
  assert.equal(formatGpuLaunchOutcome("startup-configured"), uiText.softwareDetail.gpuLaunchOutcome.configured);
  for (const outcome of ["provider-ready", "pass-through-started", "provider-unavailable-fallback",
    "bootstrap-failed-fallback", "provider-timeout", "debugger-detach-failed", "recursion-blocked", "launch-failed"])
    assert.notEqual(formatGpuLaunchOutcome(outcome), uiText.softwareDetail.gpuLaunchOutcome.unknown);
  assert.equal(Object.keys(reasons).length, 17);
  for (const [code, value] of Object.entries(reasons)) {
    assert(value.length > 0, `${language.id}: ${code}`);
    assert.equal(gpuPlacementReason(code), value);
    if (!["zh-CN", "zh-TW"].includes(language.id)) assert(!value.includes("已保存设置"), `${language.id}: ${code}`);
  }
  const d3d11 = formatGpuProviderCapability(uiText.softwareDetail.providerPhase.startup, {
    state: "supported", providerId: "provider", graphicsApi: "D3D11", architecture: "x64",
    reasonCode: "startup-d3d-creation-only", missingFiles: []
  });
  assert(d3d11.includes("D3D11CreateDeviceAndSwapChain"));
  assert(!d3d11.includes("DeviceFactory"));
  const d3d12 = formatGpuProviderCapability(uiText.softwareDetail.providerPhase.startup, {
    state: "supported", providerId: "provider", graphicsApi: "D3D12", architecture: "x64",
    reasonCode: "startup-d3d-creation-only", missingFiles: []
  });
  assert(d3d12.includes("D3D12GetInterface"));
  assert(d3d12.includes(reasons["d3d12-factory-limit"]));
  const missing = formatGpuProviderCapability(uiText.softwareDetail.providerPhase.startup, {
    state: "unsupported", providerId: "provider", graphicsApi: "Vulkan",
    reasonCode: "startup-provider-missing", missingFiles: ["厂商 Layer.dll", "layer.json"]
  });
  assert(missing.includes("厂商 Layer.dll, layer.json"));
  assert(missing.includes(reasons["startup-provider-missing"]));
  const target = startupTargetGpuOptionsForSoftwarePolicy(true, "gpu2", [
    { targetGpu: "gpu2", adapterName: "真实 Device 名称", available: false, reasonCode: "target-missing" }
  ]).find(([id]) => id === "gpu2")!;
  assert(target[1].includes("真实 Device 名称"));
  assert(target[1].includes(reasons["target-missing"]));
  assert.equal(target[2], true);
}
console.log("PASS GPU placement presentation in every selectable language");
