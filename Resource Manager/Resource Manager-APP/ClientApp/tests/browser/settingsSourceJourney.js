export default async function settingsSourceJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const failures = [];
  const settingsPatchBodies = [];
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  page.on("request", (request) => {
    if (request.method() === "PATCH"
      && new URL(request.url()).pathname === "/api/settings/app") {
      settingsPatchBodies.push(request.postDataJSON());
    }
  });
  page.on("console", (message) => {
    const text = message.text();
    const expectedConflictResponse = text.includes("status of 409 (Conflict)");
    if (message.type() === "error" && !expectedConflictResponse) {
      failures.push(`console: ${text}`);
    }
  });

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const postJson = async (path, value) => {
    const result = await page.evaluate(async ({ path, value }) => {
      const response = await fetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(value)
      });
      return { ok: response.ok, status: response.status, body: await response.text() };
    }, { path, value });
    assert(result.ok, `POST ${path} failed: ${JSON.stringify(result)}`);
    return JSON.parse(result.body);
  };
  const requestTelemetry = () => page.evaluate(async () => {
    const response = await fetch("/__test/requests");
    return response.json();
  });
  const isSelected = async (locator) => (await locator.getAttribute("aria-checked")) === "true";

  await page.setViewportSize({ width: 1100, height: 760 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await postJson("/__test/scenario", {
    capabilities: "ready",
    settings: "ready",
    settingsConflict: false,
    settingsGetDelayMs: 0,
    settingsPatchDelayMs: 0,
    settingsRevision: "frontend-harness-r1",
    settingsTheme: "system"
  });
  await postJson("/__test/requests/reset", {});
  await page.reload({ waitUntil: "domcontentloaded" });

  await page.getByRole("button", { name: "设置", exact: true }).click();
  const settingsBoundary = page.locator(".settings-readonly-boundary");
  await settingsBoundary.waitFor({ state: "visible", timeout: 7000 });
  await page.getByRole("button", { name: "外观", exact: true }).click();

  const systemTheme = page.getByRole("radio", { name: "跟随系统", exact: true });
  const lightTheme = page.getByRole("radio", { name: "浅色", exact: true });
  const darkTheme = page.getByRole("radio", { name: "深色", exact: true });
  const lowContrastTheme = page.getByRole("radio", { name: "低对比", exact: true });
  const typeBarColor = page.getByRole("radio", { name: "类型固定色", exact: true });
  const distinctBarColor = page.getByRole("radio", { name: "异色区分", exact: true });
  await systemTheme.waitFor({ state: "visible", timeout: 3000 });
  await typeBarColor.waitFor({ state: "visible", timeout: 3000 });
  assert(await isSelected(systemTheme), "Initial authoritative theme is not selected");
  assert(await isSelected(typeBarColor), "Initial authoritative resource-bar color mode is not selected");
  assert(await page.locator("body").getAttribute("data-bar-color") === "type",
    "Initial committed resource-bar color mode was not published");

  const startupTelemetry = await requestTelemetry();
  const startupSettingsRequests = startupTelemetry["/api/settings/app"];
  assert(startupSettingsRequests?.started === 1 && startupSettingsRequests?.completed === 1,
    `Settings has more than one startup owner: ${JSON.stringify(startupSettingsRequests)}`);

  await darkTheme.click();
  await distinctBarColor.click();
  assert(await isSelected(darkTheme), "Dark theme draft was not selected");
  assert(await isSelected(distinctBarColor), "Distinct resource-bar color draft was not selected");
  assert(await page.locator("html").getAttribute("data-theme") === "system",
    "A draft theme changed the committed presentation before save");
  assert(await page.locator("body").getAttribute("data-bar-color") === "type",
    "A draft resource-bar color mode changed the committed presentation before save");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  const savedStatus = page.getByRole("status").filter({ hasText: "已保存" });
  await savedStatus.waitFor({ state: "visible", timeout: 5000 });
  assert(await page.locator("html").getAttribute("data-theme") === "dark",
    "Accepted save was not published through the authoritative settings source");
  assert(await page.locator("body").getAttribute("data-bar-color") === "distinct",
    "Accepted resource-bar color mode was not published through committed settings");
  assert(settingsPatchBodies[0]?.changes?.appearance?.barColorMode === "distinct",
    `First settings patch omitted the distinct resource-bar color mode: ${JSON.stringify(settingsPatchBodies[0])}`);

  await postJson("/__test/scenario", {
    settingsRevision: "frontend-harness-r3",
    settingsTheme: "light"
  });
  await lowContrastTheme.click();
  await typeBarColor.click();
  await page.getByRole("button", { name: "保存", exact: true }).click();

  const conflictAlert = page.getByRole("alert").filter({
    hasText: "设置已在其他位置更新，请重新加载"
  });
  await conflictAlert.waitFor({ state: "visible", timeout: 5000 });
  assert(await isSelected(lowContrastTheme), "Revision conflict discarded the local draft");
  assert(await isSelected(typeBarColor), "Revision conflict discarded the local resource-bar color draft");
  assert(await page.locator("html").getAttribute("data-theme") === "light",
    "Revision conflict did not publish the newer committed settings");
  assert(await page.locator("body").getAttribute("data-bar-color") === "distinct",
    "Revision conflict published the rejected resource-bar color draft");

  await page.getByRole("button", { name: "重新加载", exact: true }).click();
  await conflictAlert.waitFor({ state: "hidden", timeout: 5000 });
  await page.waitForFunction(() => Array.from(document.querySelectorAll("[role='radio']"))
    .some((element) => element.textContent?.trim() === "浅色"
      && element.getAttribute("aria-checked") === "true"), null, { timeout: 5000 });
  assert(await isSelected(lightTheme), "Reload did not replace the conflicted draft");
  assert(!await isSelected(lowContrastTheme), "Reload retained a superseded conflicted draft");
  assert(await isSelected(distinctBarColor), "Reload did not restore the committed resource-bar color mode");
  assert(!await isSelected(typeBarColor), "Reload retained the rejected resource-bar color draft");

  await typeBarColor.click();
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await page.waitForFunction(() => document.body.dataset.barColor === "type", null, { timeout: 5000 });
  assert(settingsPatchBodies[2]?.changes?.appearance?.barColorMode === "type",
    `Final settings patch omitted the fixed-type resource-bar color mode: ${JSON.stringify(settingsPatchBodies[2])}`);

  const finalTelemetry = await requestTelemetry();
  const settingsRequests = finalTelemetry["/api/settings/app"];
  assert(settingsRequests?.started === 5 && settingsRequests?.completed === 5,
    `Unexpected settings request count: ${JSON.stringify(settingsRequests)}`);
  assert(JSON.stringify(settingsRequests.statuses) === JSON.stringify([200, 200, 409, 200, 200]),
    `Unexpected settings request sequence: ${JSON.stringify(settingsRequests.statuses)}`);
  assert(settingsRequests.maxInFlight === 1,
    `Settings requests overlapped unexpectedly: ${JSON.stringify(settingsRequests)}`);

  await page.getByRole("button", { name: "系统集成", exact: true }).click();
  const autoStart = page.getByRole("checkbox", { name: "开机自启", exact: true });
  assert(!await autoStart.isChecked(), "Autostart must default to disabled");
  await postJson("/__test/scenario", { settingsPatchDelayMs: 800 });
  await autoStart.check();
  const acknowledgement = page.waitForResponse((response) => response.request().method() === "PATCH"
    && new URL(response.url()).pathname === "/api/settings/app");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await autoStart.uncheck();
  await acknowledgement;
  await page.getByRole("button", { name: "保存", exact: true }).waitFor({ state: "visible" });
  assert(!await autoStart.isChecked(), "An older save response erased the newer autostart draft");
  const persisted = await page.evaluate(async () => (await (await fetch("/api/settings/app")).json()).settings);
  assert(persisted.systemIntegration.autoStartEnabled === true, "The submitted autostart value was not stored");
  await postJson("/__test/scenario", { settingsPatchDelayMs: 0 });
  for (const enabled of [false, true]) {
    await autoStart.setChecked(enabled);
    const saved = page.waitForResponse((response) => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: "保存", exact: true }).click();
    await saved;
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: "设置", exact: true }).click();
    await page.getByRole("button", { name: "系统集成", exact: true }).click();
    await autoStart.waitFor({ state: "visible" });
    assert(await autoStart.isChecked() === enabled, `Autostart ${enabled} did not survive reload`);
  }
  await page.getByRole("button", { name: "外观", exact: true }).click();
  for (const name of ["动画效果", "资源条硬件加速", "文字平滑"]) {
    assert(await page.getByRole("radiogroup", { name }).isVisible(),
      `${name} has a persisted setting but no usable control`);
  }
  await page.getByRole("radiogroup", { name: "动画效果" })
    .getByRole("radio", { name: "无动画" }).click();
  await page.getByRole("radiogroup", { name: "资源条硬件加速" })
    .getByRole("radio", { name: "始终关闭" }).click();
  await page.getByRole("radiogroup", { name: "文字平滑" })
    .getByRole("radio", { name: "灰度平滑" }).click();
  const appearanceSave = page.waitForResponse((response) => response.request().method() === "PATCH"
    && new URL(response.url()).pathname === "/api/settings/app");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await appearanceSave;
  const appearancePatch = settingsPatchBodies.at(-1)?.changes?.appearance;
  assert(appearancePatch?.animations === "none"
    && appearancePatch?.resourceBarHardwareAccelerationMode === "disabled"
    && appearancePatch?.fontSmoothing === "grayscale",
    `Restored appearance controls did not save their values: ${JSON.stringify(appearancePatch)}`);
  await page.getByRole("button", { name: "性能", exact: true }).click();
  assert(await page.getByText("自动调度性能优化", { exact: true }).count() === 0
    && await page.getByText("单显卡启动拦截", { exact: true }).count() === 0,
    "Retired scheduling optimization control is still visible");
  const gpuUseCases = page.getByRole("group", { name: "GPU 用途" });
  assert(await gpuUseCases.isVisible(), "GPU performance use cases have no usable control");
  assert(await gpuUseCases.getByRole("checkbox", { name: "综合" }).isChecked(),
    "The saved default GPU use case is not selected");
  await gpuUseCases.getByRole("checkbox", { name: "AI" }).click();
  const performanceSave = page.waitForResponse((response) => response.request().method() === "PATCH"
    && new URL(response.url()).pathname === "/api/settings/app");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await performanceSave;
  assert(JSON.stringify(settingsPatchBodies.at(-1)?.changes?.performance?.gpuPerformanceUseCases)
    === JSON.stringify(["general", "ai"]),
    "GPU performance use cases did not save both selected values");
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    startupReadOwners: startupSettingsRequests.started,
    savePublishedAuthoritatively: true,
    conflictPreservedDraft: true,
    reloadConverged: true,
    resourceBarColorModesPersisted: ["distinct", "type"],
    requestStatuses: settingsRequests.statuses,
    autoStartSavedAndReloaded: [false, true],
    editDuringSavePreserved: true,
    pageErrors: failures
  };
}
