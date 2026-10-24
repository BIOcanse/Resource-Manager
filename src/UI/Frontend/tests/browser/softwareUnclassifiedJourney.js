import assert from "node:assert/strict";

export default async function softwareUnclassifiedJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  let classified = false;
  const writes = [];
  const issue = { id: "classification:unclassified", kind: "Unclassified", severity: "Info",
    source: "RuntimeAttribution", label: "", message: "", dynamic: false, references: [], reportIds: [] };
  const software = ["runtime_product", "runtime_package", "runtime_root"].map((kind, index) => ({
    id: `runtime:${index}`, name: `Runtime App ${index}`, kind, displayKind: "other", state: "runtime",
    sources: [kind], rootPaths: index === 0 ? [] : [`C:/Apps/Runtime${index}`], message: "",
    operations: { canUninstall: false, uninstallKind: "None", uninstallLabel: "", uninstallMessage: "" },
    issues: [issue]
  }));
  await page.route("**/api/software", route => route.fulfill({ json: software.map((item, index) =>
    classified && index === 0 ? { ...item, id: "manual:0", kind: "other", issues: [] } : item) }));
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
  assert.deepEqual(errors, []);
  return { runtimeSources: 3, leaveWithoutWrite: true, categorySelection: true, sourceIdPreserved: true, reminderCleared: true };
}
