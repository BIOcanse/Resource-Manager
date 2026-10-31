import type { HostMemoryProtectionSettings, HostMemoryProtectionStatus } from "../../src/data/memoryProtection/memoryProtectionTypes.ts";
import type { GpuPlacementSoftwarePolicy } from "../../src/types.ts";
type MemoryPatch = { changes: { memoryProtection: Partial<HostMemoryProtectionSettings> } };
import type { Page, Locator, Route } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { memoryProtectionDecisionReasons } from "./memoryProtectionFixture.ts";
import { waitForContentSettled } from "./contentSettled.ts";

// 主机内存保护的新规则（docs/host_memory_protection_2026-10-26.md 第 5、6、10 条）在界面上的样子：
// - 设置：最小进程增长量（字节，原样发送）和进程白名单（可清空）随设置保存，后端拒绝码照已有方式显示；
// - 软件策略：“不结束”随完整策略保存，保存别的设置时保留；
// - 状态、记录和正常运行点只来自 /api/memory-protection/subscribe 的分帧：生效和保留区是否建成分开显示，
//   atOrAboveThreshold 为 null 时写明无法判断，最近一次判断和当前处理编号照后端发布的值显示，不刷新页面也会更新；
//   新记录、更新的记录按处理编号和进程身份合并并按上限截断，清除正常运行点后显示没有，全程不轮询；
// - 处理记录按 actionId 分组：进程树一组写出树根，被跳过的进程（含读不到内存的）一样列出；选择依据每组一次；
//   原因是稳定的码，诊断原文在“详情”里；
// - 设置值类型不符由设置页顶部的保存状态写明，并给出出错的路径。
// 期望值按 zh-CN 手写或取自文案，不调用被测的格式化函数。
export default async function memoryProtectionEventsJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const copy = app.memoryProtection;
  const patches: MemoryPatch[] = [];
  const statusGets: string[] = [];
  const pointGets: string[] = [];
  const channelPaths = new Set<string>();
  const policyWrites: GpuPlacementSoftwarePolicy[] = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "PATCH" && path === "/api/settings/app") patches.push(request.postDataJSON());
    if (request.method() === "GET" && path === "/api/memory-protection") statusGets.push(path);
    if (request.method() === "GET" && path === "/api/memory-protection/normal-running-point") pointGets.push(path);
    if (request.method() === "POST" && path === "/api/subscriptions/stream") {
      for (const item of (request.postDataJSON() as { subscriptions: { path: string }[] }).subscriptions) channelPaths.add(item.path);
    }
    if (request.method() === "PUT" && path.endsWith("/frontend-harness.software/policy")) policyWrites.push(request.postDataJSON());
  });
  await scenario({ gpuPlacementEnabled: true, runtimeEffectOwners: true });
  await page.setViewportSize({ width: 1280, height: 800 });

  async function scenario(body: Record<string, unknown>) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    assert.equal(response.ok, true, `scenario rejected: ${JSON.stringify(body)}`);
  }
  async function openSettings(name: string) {
    await scenario({ memoryProtection: name });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settingsText.sections.memoryProtection, exact: true }).click();
    await page.getByText(copy.intro, { exact: true }).waitFor();
    await panel().locator(".memory-protection-effective").waitFor();
    await waitForContentSettled(page);
  }
  const panel = () => page.locator(".memory-protection-settings");
  const state = () => panel().locator(".memory-protection-state");
  const fact = (scope: Locator, label: string) => scope.locator(".memory-protection-fact")
    .filter({ has: page.locator("dt").getByText(label, { exact: true }) }).locator("dd").first();
  const factText = async (scope: Locator, label: string) => (await fact(scope, label).innerText()).trim();
  const waitText = (locator: Locator, text: string) => locator.getByText(text, { exact: true }).waitFor();
  const save = async () => {
    const saved = page.waitForResponse(response => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: settingsText.actions.save, exact: true }).click();
    return (await saved).status();
  };
  const backendSettings = async () => (await (await fetch(`${baseUrl}/api/settings/app`)).json() as { settings: { memoryProtection: HostMemoryProtectionSettings } }).settings.memoryProtection;
  const minimum = () => panel().getByRole("textbox", { name: copy.minimumGrowthTitle, exact: true });
  const protectedRow = () => panel().locator(".memory-protection-protected");
  const entry = (index: number) => protectedRow().getByRole("textbox", { name: `${copy.protectedEntryLabel(index)} · ${copy.protectedEntry}`, exact: true });
  const removeEntry = (index: number) => protectedRow().getByRole("button", { name: `${copy.protectedEntryLabel(index)} · ${copy.removeProtected}`, exact: true });
  const rejected = (code: keyof typeof copy.reasons) => panel().getByRole("alert").getByText(copy.rejected(copy.reasons[code]), { exact: true }).waitFor();

  // 1. 两项新设置：默认值来自后端（1 GiB、explorer.exe）；0 字节和空白项被后端拒绝并显示原因；
  //    合法值原样保存；白名单可以清空，空列表照样保存。
  await openSettings("disabled-ready");
  assert.equal(await minimum().inputValue(), "1073741824");
  assert.equal((await panel().locator(".memory-protection-byte-control .memory-protection-size-readout").innerText()).trim(), "1.00 GiB");
  assert.equal(await entry(1).inputValue(), "explorer.exe");
  await minimum().fill("0");
  await minimum().press("Enter");
  assert.equal(await save(), 409);
  assert.deepEqual(patches.at(-1)!.changes, { memoryProtection: { minimumProcessGrowthBytes: 0 } });
  await rejected("invalid-minimum-process-growth-bytes");
  await minimum().fill("536870912");
  await minimum().press("Enter");
  assert.equal((await panel().locator(".memory-protection-byte-control .memory-protection-size-readout").innerText()).trim(), "512 MiB");
  await protectedRow().getByRole("button", { name: copy.addProtected, exact: true }).click();
  assert.equal(await entry(2).inputValue(), "");
  assert.equal(await save(), 409);
  assert.deepEqual(patches.at(-1)!.changes.memoryProtection,
    { minimumProcessGrowthBytes: 536870912, protectedProcesses: ["explorer.exe", ""] }, "the blank entry is sent as entered");
  await rejected("invalid-protected-processes");
  await entry(2).fill("C:\\Tools\\keep.exe");
  await entry(2).press("Tab");
  assert.equal(await save(), 200);
  assert.deepEqual(patches.at(-1)!.changes.memoryProtection,
    { minimumProcessGrowthBytes: 536870912, protectedProcesses: ["explorer.exe", "C:\\Tools\\keep.exe"] });
  assert.deepEqual(await backendSettings().then(value => [value.minimumProcessGrowthBytes, value.protectedProcesses]),
    [536870912, ["explorer.exe", "C:\\Tools\\keep.exe"]]);
  await panel().getByRole("alert").waitFor({ state: "detached" });
  await removeEntry(2).click();
  await removeEntry(1).click();
  await waitText(protectedRow(), copy.protectedEmpty);
  assert.equal(await save(), 200);
  assert.deepEqual(patches.at(-1)!.changes.memoryProtection, { protectedProcesses: [] }, "an emptied whitelist is sent as an empty list");
  assert.deepEqual((await backendSettings()).protectedProcesses, []);
  // 状态订阅随保存发布新值：生效状态由后端给出（disabled-ready 未开启）。
  assert.equal((await panel().locator(".memory-protection-effective").innerText()).trim(), copy.inactive(copy.reasons.disabled));

  // 2. 状态：保留区在运行中出错，保护仍然生效，保留区未建成，两项分开显示；提交量读不到时写明无法判断。
  await openSettings("tree-actions");
  const effective = panel().locator(".memory-protection-effective");
  assert.equal((await effective.innerText()).trim(), copy.effective);
  assert.equal(await effective.getAttribute("data-active"), "true");
  assert.equal(await factText(state(), copy.reserveTitle), `${copy.reserveNotBuilt} · ${copy.reasons["reserve-release-failed"]}`);
  assert.equal(await factText(state(), copy.thresholdState), copy.thresholdStateUnknown);
  assert.equal(await factText(state(), copy.currentAction), copy.currentActionNone);
  assert.equal(await factText(state(), copy.decisionTitle), copy.decisionReasons["commit-unavailable"]);
  assert.equal(await state().locator("[title]").count(), 0, "the protection state has no native tooltips");
  const hint = state().getByRole("button", { name: copy.effectiveHintLabel, exact: true });
  await hint.focus();
  const popover = page.locator(".help-hint-popover:not([data-exit-ghost])");
  await popover.waitFor();
  assert.equal((await popover.textContent())!.trim(), copy.effectiveHint);
  await page.keyboard.press("Escape");

  // 3. 处理记录按 actionId 分组，组按后端顺序；进程树一组写出树根，四个成员按后端顺序列出，
  //    其中两个被跳过（白名单里的树根、设为不结束且读不到内存的进程）。
  const status = await (await fetch(`${baseUrl}/api/memory-protection`)).json() as HostMemoryProtectionStatus;
  const groups = panel().locator(".memory-protection-action");
  assert.equal(await groups.count(), 2);
  const tree = groups.nth(0);
  assert.equal(await tree.getAttribute("data-target-kind"), "processTree");
  assert.equal(await tree.getAttribute("data-action-id"), status.actions[0].actionId);
  assert.equal((await tree.locator(".memory-protection-action-title").innerText()).trim(), copy.targetKinds.processTree);
  assert.equal(await factText(tree, copy.treeRoot), copy.recordProcess("launcher", 300));
  assert.equal(await factText(tree, copy.treeRootStartKey), "3000");
  assert.equal(await factText(tree, copy.actionId), status.actions[0].actionId);
  const members = tree.locator(".memory-protection-record");
  assert.equal(await members.count(), 4);
  const expectedMembers: [string, number, keyof typeof copy.outcomes, string][] = [
    ["launcher", 300, "excluded", copy.exclusionReasons["protected-process"]],
    ["game-client", 303, "excluded", copy.exclusionReasons["software-never-terminate"]],
    ["cat", 302, "terminated-without-dump", copy.reasons["process-tree"]],
    ["cat", 301, "terminated-without-dump", copy.reasons["process-tree"]]
  ];
  for (const [index, [name, processId, outcome, reason]] of expectedMembers.entries()) {
    const row = members.nth(index);
    assert.equal((await row.locator(".memory-protection-item-head strong").innerText()).trim(), copy.recordProcess(name, processId));
    assert.equal(await row.getAttribute("data-outcome"), outcome);
    assert.equal((await row.locator(".memory-protection-outcome").innerText()).trim(), copy.outcomes[outcome]);
    assert.equal(await factText(row, copy.recordReason), reason);
  }
  const unreadable = members.nth(1);
  for (const label of [copy.recordBaseline, copy.recordCurrent, copy.recordGrowth]) {
    assert.equal(await factText(unreadable, label), copy.valueNotProvided, `skipped member without memory: ${label}`);
  }
  assert.equal(await factText(members.nth(2), copy.recordGrowth), "20 B");
  const single = groups.nth(1);
  assert.equal(await single.getAttribute("data-target-kind"), "process");
  assert.equal((await single.locator(".memory-protection-action-title").innerText()).trim(), copy.targetKinds.process);
  assert.equal(await fact(single, copy.treeRoot).count(), 0, "a single-process action has no tree root");
  assert.equal(await single.locator(".memory-protection-record").count(), 1);
  const singleRecord = single.locator(".memory-protection-record");
  assert.equal(await factText(singleRecord, copy.recordReason), copy.reasons["reserve-error"], "a run-time reserve error is one code");
  assert.equal(await factText(singleRecord, copy.recordDetail), copy.reasons["reserve-release-failed"], "its diagnostic is the detail");
  assert.equal(await fact(members.nth(2), copy.recordDetail).count(), 0, "no detail line without a detail");
  // 选择依据每组一次（组的事实里，不在成员里）：规则、规则用的排序值和比较用的最小进程增长量，照后端的值。
  assert.equal(await factText(tree, copy.selectionRule), copy.selectionRules["largest-direct-children-memory-growth"]);
  assert.equal(await factText(tree, copy.selectionChildrenGrowth), "30 B");
  assert.equal(await fact(tree, copy.selectionProcessGrowth).count(), 0, "the tree rule has no process growth");
  assert.equal(await factText(tree, copy.selectionMinimum), "1.00 GiB");
  assert.equal(await factText(single, copy.selectionRule), copy.selectionRules["process-growth-at-least-minimum"]);
  assert.equal(await factText(single, copy.selectionProcessGrowth), "700 B");
  assert.equal(await fact(single, copy.selectionChildrenGrowth).count(), 0);
  assert.equal(await factText(single, copy.selectionMinimum), "1.00 GiB");
  assert.equal(await members.locator(".memory-protection-selection-rule").count(), 0, "members do not repeat the basis");
  for (const group of [tree, single]) {
    assert.equal(await group.locator("[title]").count(), 0, "action groups have no native tooltips");
  }

  // 4. 后端每次发布都直接更新界面，不刷新页面：逐个发布六种判断原因，再发布一个正在进行的处理编号。
  for (const decisionReason of memoryProtectionDecisionReasons) {
    await scenario({ memoryProtectionDecision: { decisionReason } });
    await waitText(fact(state(), copy.decisionTitle), copy.decisionReasons[decisionReason as keyof typeof copy.decisionReasons]);
  }
  const running = "0123456789abcdef0123456789abcdef";
  await scenario({ memoryProtectionDecision: { decisionReason: null, currentActionId: running } });
  await waitText(fact(state(), copy.currentAction), running);
  assert.equal(await factText(state(), copy.decisionTitle), copy.decisionNone);

  // 5. 达到阈值、低于阈值各自照后端的布尔值显示。
  await openSettings("action-in-progress");
  assert.equal(await factText(state(), copy.thresholdState), copy.thresholdStateReached);
  assert.equal(await factText(state(), copy.currentAction), "5fb774b0d85b49259ad57b3280d2fa51");
  assert.equal(await factText(state(), copy.reserveTitle), copy.reserveBuilt);
  await openSettings("active");
  assert.equal(await factText(state(), copy.thresholdState), copy.thresholdStateBelow);

  // 6. 记录帧：容量为 2 时，新记录排在最前；满了以后新处理不再记下（不淘汰旧记录），界面照后端的未记下条数显示；
  //    同一处理编号和进程身份的记录原位更新。原因是码，诊断原文在详情里（结束失败的 Win32 错误照文案写出）。
  await scenario({ memoryProtectionRecordCapacity: 2 });
  const records = panel().locator(".memory-protection-records");
  await waitText(records, copy.recordsDescription(2));
  await waitText(records, copy.recordsEmpty);
  const recordIds = async () => records.locator(".memory-protection-record").evaluateAll(rows => rows.map(row =>
    [row.closest(".memory-protection-action")!.getAttribute("data-action-id"), row.querySelector<HTMLElement>(".memory-protection-item-head strong")!.textContent!.trim(),
      row.getAttribute("data-outcome")]));
  const waitRecords = async (expected: string[][]) => {
    for (let attempt = 0; attempt < 100 && JSON.stringify(await recordIds()) !== JSON.stringify(expected); attempt += 1) {
      await page.waitForTimeout(50);
    }
    assert.deepEqual(await recordIds(), expected);
  };
  const [first, second, third] = ["1".repeat(32), "2".repeat(32), "3".repeat(32)];
  await scenario({ memoryProtectionRecords: [{ actionId: first, processId: 61, name: "first" }] });
  await scenario({ memoryProtectionRecords: [{ actionId: second, processId: 62, name: "second" }] });
  await waitRecords([[second, copy.recordProcess("second", 62), "terminated-without-dump"],
    [first, copy.recordProcess("first", 61), "terminated-without-dump"]]);
  assert.equal(await records.getByText(copy.recordsUnrecorded(1), { exact: true }).count(), 0, "nothing unrecorded yet");
  await scenario({ memoryProtectionRecords: [{ actionId: third, processId: 63, name: "third" }] });
  await waitText(records, copy.recordsUnrecorded(1));
  await waitRecords([[second, copy.recordProcess("second", 62), "terminated-without-dump"],
    [first, copy.recordProcess("first", 61), "terminated-without-dump"]]);
  await scenario({ memoryProtectionRecords: [{ actionId: second, processId: 62, name: "second", outcome: "termination-failed",
    reason: "termination-failed", detail: "terminate-failed-5" }] });
  await waitRecords([[second, copy.recordProcess("second", 62), "termination-failed"],
    [first, copy.recordProcess("first", 61), "terminated-without-dump"]]);
  const updated = records.locator(".memory-protection-record[data-outcome='termination-failed']");
  assert.equal(await factText(updated, copy.recordReason), copy.reasons["termination-failed"]);
  assert.equal(await factText(updated, copy.recordDetail), copy.terminateFailed("5"));
  await scenario({ memoryProtectionRecordCapacity: 4 });
  await scenario({ memoryProtectionRecords: [{ actionId: first, processId: 64, name: "first-child" }] });
  await waitRecords([[first, copy.recordProcess("first-child", 64), "terminated-without-dump"],
    [first, copy.recordProcess("first", 61), "terminated-without-dump"],
    [second, copy.recordProcess("second", 62), "termination-failed"]]);
  assert.equal(await records.locator(".memory-protection-action").count(), 2, "both records of the first action form one group");

  // 7. 别处删除了生效的正常运行点：后端发 null，界面写明没有，保护随状态帧变为未生效；重新创建后完整的点由帧送达，不读 GET。
  const pointRow = panel().locator(".settings-row").filter({ has: page.getByText(copy.pointTitle, { exact: true }) });
  await pointRow.locator(".memory-protection-point").waitFor();
  const { activeId } = await (await fetch(`${baseUrl}/api/memory-protection/normal-running-points`)).json() as { activeId: string };
  assert.equal((await fetch(`${baseUrl}/api/memory-protection/normal-running-points/${activeId}`, { method: "DELETE" })).status, 204);
  await waitText(pointRow, copy.pointNone);
  await waitText(state(), copy.inactive(copy.reasons["no-normal-running-point"]));
  await pointRow.getByRole("button", { name: copy.createPoint, exact: true }).click();
  await pointRow.locator(".memory-protection-point").waitFor();
  assert.equal(await factText(pointRow, copy.pointProcessCount), "2");
  await waitText(state(), copy.effective);

  // 8. 设置值类型不符：后端以 invalid-settings-value-type 拒绝并给出路径，设置页顶部写明原因和路径；本分区不重复显示。
  const typeRoute = async (route: Route) => {
    if (route.request().method() !== "PATCH") return route.fallback();
    const sent = route.request().postDataJSON() as { changes: { memoryProtection: { minimumProcessGrowthBytes: number | string } } };
    sent.changes.memoryProtection.minimumProcessGrowthBytes = "1024";
    await route.continue({ postData: JSON.stringify(sent) });
  };
  await page.route("**/api/settings/app", typeRoute);
  await minimum().fill("2048");
  await minimum().press("Enter");
  assert.equal(await save(), 409);
  await waitText(page.locator(".settings-save-state"),
    `${settingsText.saveState.invalidValueType} · $.memoryProtection.minimumProcessGrowthBytes`);
  assert.equal(await panel().getByRole("alert").count(), 0, "the section does not repeat the settings owner's rejection");
  assert.equal((await backendSettings()).minimumProcessGrowthBytes, 1073741824, "nothing was committed");
  await page.unroute("**/api/settings/app", typeRoute);
  assert.equal(await save(), 200);
  assert.equal((await backendSettings()).minimumProcessGrowthBytes, 2048);

  assert.equal(statusGets.length, 0, "the page never polls GET /api/memory-protection");
  assert.equal(pointGets.length, 0, "the normal running point is never read by GET");
  assert(channelPaths.has("/api/memory-protection/subscribe"), "status is subscribed on the shared channel");

  // 9. 软件策略“不结束”：随完整策略保存（一个字段都不少），保存别的设置时保留；和“不转储”互不影响。
  const softwareCopy = copy.software;
  const policyPath = `${baseUrl}/api/gpu-placement/software/frontend-harness.software`;
  await scenario({ memoryProtection: "disabled-ready" });
  const before = (await (await fetch(policyPath)).json() as { softwarePolicy: GpuPlacementSoftwarePolicy }).softwarePolicy;
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: app.page.components, exact: true }).click();
  await page.getByRole("tablist", { name: app.management.categoryNav }).getByRole("tab", { name: new RegExp(app.softwareKind.other) }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" })
    .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.getByRole("tab", { name: app.softwareDetail.tab.policy, exact: true }).click();
  const group = dialog.locator(".software-memory-protection-group");
  await group.getByRole("heading", { name: softwareCopy.title, exact: true }).waitFor();
  await waitForContentSettled(page);
  const neverTerminate = group.getByRole("checkbox", { name: softwareCopy.neverTerminate, exact: true });
  const noDump = group.getByRole("checkbox", { name: softwareCopy.noDump, exact: true });
  await waitText(group, softwareCopy.neverTerminateHint);
  assert.equal(await neverTerminate.isChecked(), false, "default is off");
  const savePolicy = async () => {
    const saved = page.waitForResponse(response => response.request().method() === "PUT" && response.url().endsWith("/policy"));
    await dialog.getByRole("button", { name: app.softwareDetail.save, exact: true }).click();
    assert.equal((await saved).status(), 200);
    await dialog.getByRole("button", { name: app.softwareDetail.save, exact: true }).and(page.locator(":disabled")).waitFor();
  };
  await neverTerminate.check();
  await savePolicy();
  const written = policyWrites.at(-1)!;
  assert.deepEqual(Object.keys(written).sort(), Object.keys(before).sort(), "the full policy is sent, no field dropped");
  assert.equal(written.hostMemoryProtectionNeverTerminate, true);
  assert.equal(written.hostMemoryProtectionNoDump, before.hostMemoryProtectionNoDump);
  for (const key of (Object.keys(before) as (keyof GpuPlacementSoftwarePolicy)[]).filter(key => !["hostMemoryProtectionNeverTerminate", "updatedAt"].includes(key))) {
    assert.deepEqual(written[key], before[key], `unchanged policy field ${key}`);
  }
  await noDump.check();
  await savePolicy();
  assert.equal(policyWrites.at(-1)!.hostMemoryProtectionNoDump, true);
  assert.equal(policyWrites.at(-1)!.hostMemoryProtectionNeverTerminate, true, "saving another setting keeps never-terminate");
  assert.equal((await (await fetch(policyPath)).json() as { softwarePolicy: GpuPlacementSoftwarePolicy }).softwarePolicy.hostMemoryProtectionNeverTerminate, true);

  assert.equal(errors.length, 0, `page errors: ${errors.join("\n")}`);
  return { settingsRejections: ["invalid-minimum-process-growth-bytes", "invalid-protected-processes", "invalid-settings-value-type"],
    emptiedWhitelist: true, groups: 2, treeMembers: 4, decisionReasons: memoryProtectionDecisionReasons.length,
    recordFrames: 5, pointCleared: true, policyWrites: policyWrites.length };
}
