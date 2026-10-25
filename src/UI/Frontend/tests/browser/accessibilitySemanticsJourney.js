import assert from "node:assert/strict";

/**
 * 读屏语义与键盘，真实页面上逐项验收：
 * - 软件设置的「进程」列表框：只有选中项在 Tab 顺序里，上下键、Home/End 移动焦点，选中跟着焦点走；
 * - 对话框打开时页面其余部分 inert，关闭后恢复。
 */
export default async function accessibilitySemanticsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const processes = ["alpha.exe", "beta.exe", "gamma.exe"].map((processName, index) => ({
    processKey: `frontend-harness.software:${processName}`,
    processName,
    executablePath: `C:\\Harness\\${processName}`,
    architecture: "x64",
    observationCount: index + 1,
    lastProcessId: 4100 + index,
    evidenceSources: ["frontend-harness"],
    confidence: 1
  }));
  // 「策略」「进程」分页只在 GPU 调度可用时出现。
  const scenario = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true }) });
  assert.ok(scenario.ok, "Could not enable GPU placement in the fixture");
  await page.route("**/api/gpu-placement/software/frontend-harness.software**", async route => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    const data = await response.json();
    data.processHistory = { ...data.processHistory, processes };
    return route.fulfill({ json: data });
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });

  // 进程列表框。
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" }).getByRole("button", { name: /一般应用/ }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" }).getByRole("button", { name: "软件设置", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.waitFor();
  const shellInert = () => page.evaluate(() => Boolean(document.querySelector(".app-shell")?.closest("[inert]")));
  assert.equal(await shellInert(), true, "The application behind an open dialog stays interactive");
  await dialog.getByRole("tab", { name: "进程", exact: true }).click();
  const list = dialog.getByRole("listbox", { name: "历史进程" });
  await list.waitFor();
  const options = list.getByRole("option");
  assert.equal(await options.count(), 3);
  const state = () => list.evaluate((element) => {
    const all = [...element.querySelectorAll("[role='option']")];
    return {
      tabbable: all.filter(option => option.tabIndex === 0).map(option => option.querySelector("strong")?.textContent),
      focused: document.activeElement?.closest("[role='option']")?.querySelector("strong")?.textContent ?? null,
      selected: all.filter(option => option.getAttribute("aria-selected") === "true").map(option => option.querySelector("strong")?.textContent),
      editor: element.closest(".process-policy-layout")?.querySelector(".process-policy-editor h4")?.textContent ?? null
    };
  });
  await page.waitForFunction(() => document.querySelector(".process-policy-editor h4")?.textContent === "alpha.exe");
  assert.deepEqual((await state()).tabbable, ["alpha.exe"], "Only the selected process is in the Tab order");
  await options.first().focus();
  const visited = [];
  for (const key of ["ArrowDown", "End", "Home", "ArrowUp", "ArrowDown"]) {
    await page.keyboard.press(key);
    await page.waitForFunction(() => {
      const focused = document.activeElement?.closest("[role='option']");
      return focused?.getAttribute("aria-selected") === "true"
        && document.querySelector(".process-policy-editor h4")?.textContent === focused.querySelector("strong")?.textContent;
    });
    const current = await state();
    assert.deepEqual(current.tabbable, [current.focused], `${key}: the focused process is the only tabbable one`);
    assert.deepEqual(current.selected, [current.focused], `${key}: selection follows focus`);
    visited.push(current.focused);
  }
  assert.deepEqual(visited, ["beta.exe", "gamma.exe", "alpha.exe", "gamma.exe", "alpha.exe"], `Process listbox keys: ${JSON.stringify(visited)}`);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "detached" });
  assert.equal(await shellInert(), false, "Closing the dialog did not restore the application");

  assert.deepEqual(errors, []);
  return { processKeys: visited, dialogInertRestored: true };
}
