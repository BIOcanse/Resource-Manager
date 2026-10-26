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
      { id: "gpu.0.usage", unit: "%", deviceIdentity: "opaque-a", selectable: true, detail: "untrusted old detail", candidateSources: [] },
      { id: "gpu.0.vram", unit: "B", deviceIdentity: "opaque-a", selectable: true, candidateSources: [] },
      { id: "gpu.1.usage", unit: "%", deviceIdentity: "opaque-b", selectable: true, candidateSources: [] },
      { id: "gpu.2.usage", unit: "%", deviceIdentity: "absent", selectable: true, candidateSources: [] }
    ] });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  // 第一张还有剩余单位的卡片：点“+”，大小选最小，再打开选监控项的弹窗。
  await page.locator(".slot-add").first().click();
  await page.getByRole("menuitem").first().click();
  const picker = page.locator("#metricModal");
  await picker.getByRole("group", { name: "GPU0 · First GPU", exact: true }).waitFor();
  await picker.getByRole("group", { name: "GPU1 · Second GPU", exact: true }).waitFor();
  await picker.getByRole("group", { name: "GPU2", exact: true }).waitFor();
  assert.equal(await picker.getByText("untrusted old detail", { exact: true }).count(), 0);
  // 列表框里只有分组（role=group）和选项：组名不是标题角色，空状态在列表框外。
  const listbox = picker.getByRole("listbox", { name: app.metricPicker.listLabel, exact: true });
  const roles = await listbox.evaluate((element) => ({
    children: [...element.children].map((child) => child.getAttribute("role")),
    headings: element.querySelectorAll("h1, h2, h3, h4, h5, h6, [role=heading]").length
  }));
  assert.ok(roles.children.length > 0 && roles.children.every((role) => role === "group"), JSON.stringify(roles.children));
  assert.equal(roles.headings, 0, "The listbox holds no heading");
  await picker.getByRole("combobox", { name: app.metricPicker.searchLabel, exact: true }).fill("no-such-metric-zzz");
  const empty = picker.getByText(app.metricPicker.empty, { exact: true });
  await empty.waitFor();
  assert.equal(await listbox.getByText(app.metricPicker.empty, { exact: true }).count(), 0, "The empty state is outside the listbox");
  assert.equal(await listbox.evaluate((element) => element.children.length), 0, "An empty listbox has no children");
  await picker.getByRole("button", { name: app.metricPicker.cancel, exact: true }).click();
  return { gpuGroups: ["GPU0 · First GPU", "GPU1 · Second GPU", "GPU2"] };
}
