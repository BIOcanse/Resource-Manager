import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function softwareUnclassifiedJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  let classified = false;
  let removed = false;
  const removals = [];
  const writes = [];
  const issue = { id: "classification:unclassified", kind: "unclassified", severity: "info",
    source: "runtime_attribution", label: "", message: "", dynamic: false, references: [], reportIds: [] };
  const software = ["runtime_product", "runtime_package", "runtime_root"].map((kind, index) => ({
    id: `runtime:${index}`, name: `Runtime App ${index}`, kind, displayKind: "other", state: "runtime",
    sources: [kind], rootPaths: index === 0 ? [] : [`C:/Apps/Runtime${index}`], message: "",
    operations: { canUninstall: false, uninstallKind: "None", uninstallLabel: "", uninstallMessage: "" },
    issues: [issue]
  }));
  software.push({ ...software[0], id: "runtime:game", name: "Windows Game", kind: "game", displayKind: "game",
    categorySource: "Windows GameConfigStore\\Children\\MatchedExeFullPath", issues: [] });
  await page.route("**/api/software", route => route.fulfill({ json: software.map((item, index) =>
    classified && index === 0 ? { ...item, id: "manual:0", kind: "other", issues: [] } : item).filter((_, index) => !removed || index !== 0) }));
  await page.route("**/api/software/manual/*", async route => {
    assert.equal(route.request().method(), "DELETE");
    removals.push(new URL(route.request().url()).pathname);
    removed = true;
    await route.fulfill({ status: 204 });
  });
  await page.route("**/api/software/manual", async route => {
    writes.push(route.request().postDataJSON());
    classified = true;
    await route.fulfill({ json: {} });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  const nav = page.getByRole("navigation", { name: "组件和软件分类" });
  await nav.getByRole("button", { name: /^未归类/ }).click();
  await page.locator(".software-item").filter({ hasText: "Runtime App 2" }).waitFor();
  assert.equal(await page.locator(".software-item").count(), 3);
  for (const method of ["通过程序产品名称识别", "通过运行中的应用包识别", "通过运行中的安装根目录识别"])
    await page.getByText(method, { exact: true }).waitFor();
  await page.getByText("C:/Apps/Runtime1", { exact: true }).waitFor();
  const card = page.locator(".software-item").filter({ hasText: "Runtime App 0" });
  await card.getByRole("button", { name: "选择分类", exact: true }).click();
  let dialog = page.getByRole("dialog", { name: "添加一般应用" });
  await dialog.waitFor();
  await dialog.getByRole("button", { name: "取消", exact: true }).click();
  assert.equal(writes.length, 0, "Leaving the reminder must not save a category");
  await card.getByRole("button", { name: "选择分类", exact: true }).click();
  dialog = page.getByRole("dialog", { name: "添加一般应用" });
  await dialog.getByRole("combobox", { name: "组件和软件分类", exact: true }).click();
  await page.getByRole("option", { name: "游戏", exact: true }).click();
  await page.getByRole("dialog", { name: "添加游戏" }).waitFor();
  await page.getByRole("combobox", { name: "组件和软件分类", exact: true }).click();
  await page.getByRole("option", { name: "一般应用", exact: true }).click();
  await page.getByRole("dialog", { name: "添加一般应用" }).getByRole("button", { name: "确定", exact: true }).click();
  await page.getByRole("dialog").waitFor({ state: "hidden" });
  await page.waitForFunction(() => document.querySelectorAll(".software-item").length === 2);
  assert.deepEqual(writes, [{ name: "Runtime App 0", kind: "other", rootPaths: [], sourceSoftwareId: "runtime:0" }]);
  await nav.getByRole("button", { name: /^一般应用/ }).click();
  await page.locator(".software-item").filter({ hasText: "Runtime App 0" }).waitFor();
  assert.equal(await page.locator(".software-item .software-issue-tag").count(), 0);

  const manualCard = page.locator(".software-item").filter({ hasText: "Runtime App 0" });
  await manualCard.getByRole("button", { name: "移除手动登记", exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "仅移除此手动登记及分类" }).getByRole("button", { name: "取消", exact: true }).click();
  assert.equal(removals.length, 0, "Cancel must preserve the manual record");
  await manualCard.getByRole("button", { name: "软件设置", exact: true }).click();
  const manualDetails = page.getByRole("dialog", { name: "Runtime App 0", exact: true });
  await manualDetails.getByRole("button", { name: "移除手动登记", exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "仅移除此手动登记及分类" }).getByRole("button", { name: "确认", exact: true }).click();
  await manualDetails.waitFor({ state: "hidden" });
  await manualCard.waitFor({ state: "hidden" });
  assert.deepEqual(removals, ["/api/software/manual/manual%3A0"]);

  const sourceLanguages = [];
  let previous = "zh-CN";
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previous)
      await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const app = await loadAppCopy(option.id);
    await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
    await page.getByRole("navigation", { name: app.management.categoryNav })
      .getByRole("button", { name: new RegExp(app.softwareKind.game) }).click();
    await page.locator(".software-item").filter({ hasText: "Windows Game" })
      .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const details = page.getByRole("dialog", { name: "Windows Game", exact: true });
    await details.getByText(app.management.categorySource, { exact: true }).waitFor();
    await details.getByText(app.management.windowsGameList, { exact: true }).waitFor();
    await details.locator(`button.icon-button[aria-label="${app.softwareDetail.close}"]`).click();
    sourceLanguages.push(option.id);
    previous = option.id;
  }
  assert.deepEqual(errors, []);
  return { runtimeSources: 3, leaveWithoutWrite: true, categorySelection: true, sourceIdPreserved: true,
    reminderCleared: true, manualRemovalConfirmed: true, sourceLanguages };
}
