import assert from "node:assert/strict";
import { baseScoreResolution } from "./baseScoreFixture.mjs";

// 软件详情 → 策略：软件基础分说明下面一行「当前生效：值，来自来源（键）」和五层来源链；进程分页：每个进程的进程基础分下面一行
// （docs/scheduling_protection_2026-10-29.md §3.2、§3.3、§6）。值来自后端的软件详情读取：页面路由在模拟后端的读取结果上
// 换上目录组 90（adapted.resource-manager）的来源链；用户的软件基础分照常存进模拟后端，保存后前端重读，显示后端给的值。
// 格式不对的来源链让读取失败并提示，不显示来源。
const softwareId = "frontend-harness.software";
const settingsPath = `/api/gpu-placement/software/${softwareId}`;
const processes = ["searchhost.exe", "helper.exe"].map((processName, index) => ({
  processKey: `${softwareId}:${processName}`,
  processName,
  executablePath: `C:\\Harness\\${processName}`,
  architecture: "x64",
  observationCount: index + 1,
  lastProcessId: 5100 + index,
  evidenceSources: ["frontend-harness"],
  confidence: 1
}));

export default async function softwareBaseScoreSourceJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true, runtimeEffectOwners: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable GPU placement and runtime controls");

  const backend = { malformed: false };
  let reads = 0;
  await page.route(url => url.pathname === settingsPath, async route => {
    if (route.request().method() !== "GET") return route.fallback();
    reads += 1;
    const data = await (await route.fetch()).json();
    const softwareOverride = data.softwarePolicy.baseScoreOverride ?? null;
    const layers = {
      type_default: { value: 35, key: "other" },
      catalog_group: { value: 90, key: "adapted.resource-manager" },
      user_software: softwareOverride === null ? undefined : { value: softwareOverride, key: softwareId }
    };
    data.baseScore = baseScoreResolution(layers);
    data.processHistory = { ...data.processHistory, processes };
    // 只有第一个进程有基础分：目录按进程给 30；第二个进程不在 processBaseScores 里，进程分页不为它编一个值。
    data.processBaseScores = {
      [processes[0].processKey]: baseScoreResolution({ ...layers,
        catalog_process: { value: 30, key: "system.background:searchhost.exe" } })
    };
    if (backend.malformed) data.baseScore = { ...data.baseScore, source: "type_default", value: 35 };
    await route.fulfill({ json: data });
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
  const card = page.locator(".software-item").filter({ hasText: "示例软件" });
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  const layers = dialog.getByRole("list", { name: "基础分来源", exact: true });
  const scoreInput = dialog.getByRole("spinbutton", { name: "软件基础分", exact: true });

  // 1 目录组决定：一行当前生效，五层都在，目录组带“生效”标记；一行和来源链在说明下面，和说明同一样式。
  await openTab("策略");
  await expectText("当前生效：90，来自软件目录组（adapted.resource-manager）");
  await expectLayers([
    "类型默认：35（other）", "软件目录组：90（adapted.resource-manager）", "软件目录（按进程）：—",
    "你对软件的设置：—", "你对进程的设置：—"], "catalog_group");
  const group = dialog.locator(".software-policy-group")
    .filter({ has: page.getByRole("spinbutton", { name: "软件基础分", exact: true }) });
  assert.deepEqual(await group.locator(":scope > *").evaluateAll(nodes => nodes.map(node => node.className)),
    ["software-policy-grid", "software-detail-hint", "software-base-score-source"], "The source sits under the hint, nothing moved");
  assert.equal(await group.locator(".software-base-score-effective").evaluate(node => node.classList.contains("software-detail-hint")), true);
  assert.equal(await scoreInput.getAttribute("placeholder"), "未设置时为 90", "The placeholder shows the value without the user's own setting");

  // 2 用户填 70：没保存时一行仍是后端的值；保存后重读，用户对软件的设置决定，目录组的 90 照样可见，占位不变。
  await scoreInput.fill("70");
  await scoreInput.press("Tab");
  await expectText("当前生效：90，来自软件目录组（adapted.resource-manager）");
  const readsBeforeSave = reads;
  const saved = page.waitForResponse(response => response.request().method() === "PUT"
    && new URL(response.url()).pathname === `${settingsPath}/policy`);
  await dialog.getByRole("button", { name: "保存", exact: true }).click();
  assert.ok((await saved).ok());
  await expectText("当前生效：70，来自你对软件的设置（frontend-harness.software）");
  assert.ok(reads > readsBeforeSave, "Saving rereads the detail");
  await expectLayers([
    "类型默认：35（other）", "软件目录组：90（adapted.resource-manager）", "软件目录（按进程）：—",
    "你对软件的设置：70（frontend-harness.software）", "你对进程的设置：—"], "user_software");
  assert.equal(await scoreInput.getAttribute("placeholder"), "未设置时为 90");

  // 3 进程分页：有基础分的进程在进程基础分下面一行；没有的进程不显示这一行。
  await dialog.getByRole("tab", { name: "进程", exact: true }).click();
  const list = dialog.getByRole("listbox", { name: "历史进程" });
  await list.getByRole("option", { name: /searchhost\.exe/ }).click();
  const processInput = dialog.getByRole("spinbutton", { name: "进程基础分", exact: true });
  await processInput.waitFor();
  await expectText("当前生效：70，来自你对软件的设置");
  assert.equal(await processInput.getAttribute("placeholder"), "未设置时为 70", "A process placeholder inherits the user's software setting");
  const editor = dialog.locator(".process-policy-editor");
  assert.equal(await editor.locator(".software-base-score-effective").count(), 1);
  await list.getByRole("option", { name: /helper\.exe/ }).click();
  await dialog.locator(".process-policy-editor h4").getByText("helper.exe", { exact: true }).waitFor();
  assert.equal(await editor.locator(".software-base-score-effective").count(), 0, "A process without a score shows no line");
  assert.equal(await processInput.getAttribute("placeholder"), "继承软件基础分");

  // 4 用户设置撤回：目录组又决定；进程分页回到目录按进程。
  await restoreSoftwareOverride();
  await reopen(() => undefined, "进程");
  await list.getByRole("option", { name: /searchhost\.exe/ }).click();
  await expectText("当前生效：30，来自软件目录（按进程）");

  // 5 格式不对的来源链：读取失败并提示，不显示来源。
  await reopen(() => { backend.malformed = true; }, "策略", false);
  await dialog.getByText("读取软件调度策略失败。", { exact: true }).waitFor();
  assert.equal(await dialog.locator(".software-base-score-source").count(), 0, "A malformed chain shows no source");
  backend.malformed = false;
  await dialog.locator("button.icon-button").first().click();
  await dialog.waitFor({ state: "detached" });

  assert.equal(errors.length, 0, `Base score source page errors: ${JSON.stringify(errors)}`);
  return { catalogGroupLine: true, userOverrideMarksUserSoftware: true, processLine: true, malformedChainFailsVisibly: true };

  async function openTab(name, waitPolicy = true) {
    await card.getByRole("button", { name: "软件设置", exact: true }).click();
    await dialog.getByRole("tab", { name, exact: true }).click();
    if (waitPolicy) await dialog.getByText("正在加载策略...", { exact: true }).waitFor({ state: "hidden" });
  }
  async function reopen(change, tab, waitPolicy = true) {
    await dialog.locator("button.icon-button").first().click();
    await dialog.waitFor({ state: "detached" });
    change();
    await openTab(tab, waitPolicy);
  }
  async function expectText(text) {
    await dialog.getByText(text, { exact: true }).waitFor();
  }
  async function expectLayers(texts, deciding) {
    await layers.waitFor();
    assert.deepEqual(await layers.locator("li > span:first-child").allTextContents(), texts);
    assert.deepEqual(await layers.locator("li[aria-current='true']").evaluateAll(nodes => nodes.map(node => node.dataset.source)),
      [deciding], "Exactly the deciding layer is marked");
    await layers.locator("li[aria-current='true']").getByText("生效", { exact: true }).waitFor();
  }
  async function restoreSoftwareOverride() {
    const current = (await (await fetch(`${baseUrl}${settingsPath}?softwareName=${encodeURIComponent("示例软件")}`)).json()).softwarePolicy;
    const response = await fetch(`${baseUrl}${settingsPath}/policy`, { method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...current, baseScoreOverride: null }) });
    assert.ok(response.ok, "Restoring the software base score");
  }
}
