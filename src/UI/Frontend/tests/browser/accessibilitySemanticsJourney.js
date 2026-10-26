import assert from "node:assert/strict";

/**
 * 读屏语义与键盘，真实页面上逐项验收：
 * - 软件设置的「进程」列表框：只有选中项在 Tab 顺序里，上下键、Home/End 移动焦点，选中跟着焦点走；
 * - 对话框打开时页面其余部分 inert，关闭后恢复；
 * - 标准下拉框的列表框里只有选项和选项组：组用 role="group" 并由组标题命名，空状态放在列表框外面。
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
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
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

  // 下拉框的分组和空状态（强制结束快捷键的按键选择：按字符、功能键、小键盘等分组，可搜索）。
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "系统集成", exact: true }).click();
  const editor = page.locator(".settings-hotkey-editor");
  await editor.waitFor();
  if (await editor.getByRole("button", { name: "按键 1", exact: true }).count() === 0) {
    await editor.getByRole("button", { name: "添加按键" }).click();
  }
  await editor.getByRole("button", { name: "按键 1", exact: true }).click();
  const popover = page.locator(".standard-select-popover");
  await popover.waitFor();
  const structure = () => popover.evaluate((element) => {
    const listbox = element.querySelector("[role='listbox']");
    const children = [...(listbox?.children ?? [])];
    const groups = children.filter(child => child.getAttribute("role") === "group");
    return {
      childRoles: [...new Set(children.map(child => child.getAttribute("role")))].sort(),
      groups: groups.map(group => {
        const label = document.getElementById(group.getAttribute("aria-labelledby") ?? "");
        return {
          name: label?.textContent?.trim() ?? "",
          labelInside: Boolean(label && group.contains(label)),
          labelRole: label?.getAttribute("role"),
          options: group.querySelectorAll("[role='option']").length,
          otherChildren: [...group.children].filter(child => child !== label && child.getAttribute("role") !== "option").length
        };
      }),
      looseHeadings: [...(listbox?.querySelectorAll(".standard-select-group") ?? [])].filter(heading => !heading.closest("[role='group']")).length,
      emptyInside: Boolean(listbox?.querySelector(".standard-select-empty")),
      emptyOutside: Boolean(element.querySelector(".standard-select-empty")),
      optionCount: listbox?.querySelectorAll("[role='option']").length ?? 0
    };
  });
  const grouped = await structure();
  assert.ok(grouped.groups.length >= 2, `The key list is grouped: ${JSON.stringify(grouped)}`);
  assert.deepEqual(grouped.childRoles.filter(role => role !== "option" && role !== "group"), [], `Listbox children: ${grouped.childRoles}`);
  for (const group of grouped.groups) {
    assert.ok(group.name && group.labelInside && group.labelRole === "presentation" && group.options > 0 && group.otherChildren === 0,
      `Option group semantics: ${JSON.stringify(group)}`);
  }
  assert.equal(grouped.looseHeadings, 0);
  assert.equal(grouped.emptyOutside, false);
  await popover.getByRole("combobox").fill("zzzz-no-such-key");
  await popover.locator(".standard-select-empty").waitFor();
  const empty = await structure();
  assert.deepEqual({ emptyInside: empty.emptyInside, emptyOutside: empty.emptyOutside, optionCount: empty.optionCount, childRoles: empty.childRoles },
    { emptyInside: false, emptyOutside: true, optionCount: 0, childRoles: [] }, "The empty state sits outside the listbox");
  await page.keyboard.press("Escape");
  await popover.waitFor({ state: "detached" });

  assert.deepEqual(errors, []);
  return { processKeys: visited, dialogInertRestored: true, optionGroups: grouped.groups.map(group => group.name), emptyOutsideListbox: true };
}
