import assert from "node:assert/strict";

export default async function gpuUnknownRuntimeJourney(page, baseUrl) {
  await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true }) });
  await page.route("**/api/settings/app", async route => {
    const response = await route.fetch();
    const data = await response.json();
    data.settings.performance = { ...(data.settings.performance ?? {}), preciseGpuPlacementEnabled: true };
    return route.fulfill({ json: data });
  });
  const writes = [];
  await page.route("**/api/gpu-placement/software/frontend-harness.software**", async route => {
    if (route.request().method() === "PUT") {
      const policy = route.request().postDataJSON();
      writes.push(policy);
      return route.fulfill({ json: policy });
    }
    const response = await route.fetch();
    const data = await response.json();
    data.targetInventory = { state: "current", exactTargets: [{ targetGpu: "GPU1", displayName: "GPU 1 Test", available: true }] };
    data.processCapabilities = [{ processKey: "fixture",
      startup: { state: "supported", providerId: "fixture", graphicsApi: "D3D11", reason: "fixture" },
      runtime: { state: "unknown", providerId: "fixture", graphicsApi: "D3D11", reason: "Requires runtime validation" } }];
    return route.fulfill({ json: data });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" }).getByRole("button", { name: /一般应用/ }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" }).getByRole("button", { name: "软件设置", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.getByRole("tab", { name: "策略", exact: true }).click();
  await dialog.getByRole("combobox", { name: "启动时 GPU", exact: true }).click();
  await page.getByRole("option", { name: "GPU 1 Test", exact: true }).click();
  const toggle = dialog.getByRole("checkbox", { name: "允许运行时切换显卡", exact: true });
  assert.equal(await toggle.isEnabled(), true);
  await toggle.check();
  const method = dialog.getByRole("combobox", { name: "显卡切换方式", exact: true });
  assert.equal(await method.isEnabled(), true);
  await method.click();
  await page.getByRole("option", { name: "重新加载窗口画面", exact: true }).click();
  const completedSave = page.waitForResponse(response => response.request().method() === "PUT"
    && response.url().endsWith("/policy"));
  await dialog.getByRole("button", { name: "保存", exact: true }).click();
  assert.equal((await completedSave).status(), 200);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].startupTargetGpu, "GPU1");
  assert.equal(writes[0].runtimeHotSwitchEnabled, true);
  assert.equal(writes[0].preferredRuntimeSwitchMethod, "WindowRerender");
  return { runtimeState: "unknown", savedTarget: "GPU1", savedSwitchMethod: "WindowRerender" };
}
