import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function metricPickerGpuNamesJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  // Device order and ordinals deliberately disagree with metric aliases: identity owns the join.
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices: [
      { deviceIdentity: "opaque-b", device: { kind: "gpu_adapter", address: null, displayName: "Second GPU", ordinal: 0 }, fields: [] },
      { deviceIdentity: "opaque-a", device: { kind: "gpu_adapter", address: null, displayName: "First GPU", ordinal: 1 }, fields: [] }
    ] })
  });
  assert.equal(response.status, 200);
  await page.route("**/api/metrics/catalog", async route => {
    const original = await route.fetch();
    const catalog = await original.json();
    return route.fulfill({ json: [...catalog,
      { id: "gpu.0.usage", unit: "%", deviceIdentity: "opaque-a", selectable: true, detail: "untrusted old detail" },
      { id: "gpu.0.vram", unit: "B", deviceIdentity: "opaque-a", selectable: true },
      { id: "gpu.1.usage", unit: "%", deviceIdentity: "opaque-b", selectable: true },
      { id: "gpu.2.usage", unit: "%", deviceIdentity: "absent", selectable: true }
    ] });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  await page.locator(".slot-add").first().click();
  const picker = page.locator("#metricModal");
  await picker.getByRole("group", { name: "GPU0 · First GPU", exact: true }).waitFor();
  await picker.getByRole("group", { name: "GPU1 · Second GPU", exact: true }).waitFor();
  await picker.getByRole("group", { name: "GPU2", exact: true }).waitFor();
  assert.equal(await picker.getByText("untrusted old detail", { exact: true }).count(), 0);
  await picker.getByRole("button", { name: app.metricPicker.cancel, exact: true }).click();
  return { gpuGroups: ["GPU0 · First GPU", "GPU1 · Second GPU", "GPU2"] };
}
