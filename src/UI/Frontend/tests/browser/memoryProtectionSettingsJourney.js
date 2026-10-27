import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { memoryProtectionScenarios } from "./memoryProtectionFixture.mjs";
import { waitForContentSettled } from "./contentSettled.js";

// 设置 → 主机内存保护：每个模拟场景都走一遍。后端给的触发点、剩余量照原值显示，原因码翻成文字，
// 开启被拒时显示后端原因，保留区写出簇大小与失败原因，重建、创建正常运行点、处理记录和排除的候选都能看到。
// 期望值是按 zh-CN 和默认字节模式（内存二进制、磁盘十进制）手写的字面值，不调用被测的格式化函数。
export default async function memoryProtectionSettingsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const copy = app.memoryProtection;
  const patches = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "PATCH" && path === "/api/settings/app") patches.push(request.postDataJSON());
  });
  const covered = [];
  await page.setViewportSize({ width: 1280, height: 800 });
  const timeFormat = new Intl.DateTimeFormat("zh-CN", { year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric" });

  async function open(scenario) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ memoryProtection: scenario }) });
    assert.equal(response.ok, true, `scenario ${scenario} rejected`);
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settingsText.sections.memoryProtection, exact: true }).click();
    await page.getByText(copy.intro, { exact: true }).waitFor();
    await panel().locator(".memory-protection-effective").waitFor();
    await waitForContentSettled(page);
    covered.push(scenario);
  }
  const panel = () => page.locator(".memory-protection-settings");
  const fact = (scope, label) => scope.locator(".memory-protection-fact")
    .filter({ has: page.locator("dt").getByText(label, { exact: true }) }).locator("dd").first();
  const factText = async (scope, label) => (await fact(scope, label).innerText()).trim();
  const effective = async () => (await panel().locator(".memory-protection-effective").innerText()).trim();
  const thresholdRow = () => panel().locator(".settings-row").filter({ has: page.getByText(copy.thresholdTitle, { exact: true }) });
  const pointRow = () => panel().locator(".settings-row").filter({ has: page.getByText(copy.pointTitle, { exact: true }) });
  const reserveStatus = () => panel().locator(".memory-protection-reserve-status");
  const reserveItems = () => reserveStatus().locator(".memory-protection-item");
  const totals = () => reserveStatus().locator(".memory-protection-totals");
  const enable = () => panel().getByRole("checkbox", { name: copy.enabledTitle, exact: true });
  const save = async () => {
    const saved = page.waitForResponse(response => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: settingsText.actions.save, exact: true }).click();
    return (await saved).status();
  };
  const rebuild = async () => {
    const done = page.waitForResponse(response => response.request().method() === "POST"
      && new URL(response.url()).pathname === "/api/memory-protection/reserve/rebuild");
    await panel().getByRole("button", { name: copy.rebuild, exact: true }).click();
    return (await done).status();
  };
  const expectValues = async (trigger, remaining, commit) => {
    assert.equal(await factText(thresholdRow(), copy.triggerBytes), trigger);
    assert.equal(await factText(thresholdRow(), copy.remainingBytes), remaining);
    assert.equal(await factText(thresholdRow(), copy.currentCommit), commit);
  };
  const sent = { trigger: "950 B", remaining: "750 B", commit: "200 / 1000 B（20%）" };

  // no-point：没有正常运行点；开启被拒并写明原因；创建正常运行点后显示时间和进程数。
  await open("no-point");
  assert.equal(await effective(), copy.inactive(copy.reasons["no-normal-running-point"]));
  await expectValues(sent.trigger, sent.remaining, sent.commit);
  await pointRow().getByText(copy.pointNone, { exact: true }).waitFor();
  await totals().getByText(`${copy.reserveNotBuilt} · ${copy.reasons["reserve-not-configured"]}`, { exact: true }).waitFor();
  await enable().check();
  await panel().getByText(copy.unsavedNote, { exact: true }).waitFor();
  assert.equal(await save(), 409);
  await panel().getByRole("alert").getByText(copy.rejected(copy.reasons["no-normal-running-point"]), { exact: true }).waitFor();
  assert.deepEqual(patches.at(-1).changes, { memoryProtection: { enabled: true } });
  await enable().uncheck();
  const created = page.waitForResponse(response => response.request().method() === "POST"
    && new URL(response.url()).pathname === "/api/memory-protection/normal-running-point");
  await pointRow().getByRole("button", { name: copy.createPoint, exact: true }).click();
  assert.equal((await created).status(), 200);
  await pointRow().getByText(copy.pointCreated, { exact: true }).waitFor();
  const point = await (await fetch(`${baseUrl}/api/memory-protection/normal-running-point`)).json();
  await fact(pointRow(), copy.pointAt).getByText(timeFormat.format(new Date(point.at)), { exact: true }).waitFor();
  assert.equal(await factText(pointRow(), copy.pointProcessCount), "2");
  assert.equal(await factText(pointRow(), copy.pointCommit), "200 / 1000 B");
  await page.waitForFunction(text => document.querySelector(".memory-protection-effective")?.textContent === text,
    copy.inactive(copy.reasons.disabled));

  // no-reserve：有正常运行点但没有保留区；开启被拒，原因是未设定磁盘保留区。
  await open("no-reserve");
  assert.equal(await factText(pointRow(), copy.pointProcessCount), "2");
  await enable().check();
  assert.equal(await save(), 409);
  await panel().getByRole("alert").getByText(copy.rejected(copy.reasons["reserve-not-configured"]), { exact: true }).waitFor();
  await panel().getByText(copy.reserveNotConfigured, { exact: true }).waitFor();

  // disabled-ready：两处保留区分别写出 4096 和 65536 字节的簇；开启、改处理方式都能保存并由后端回读。
  await open("disabled-ready");
  assert.equal(await effective(), copy.inactive(copy.reasons.disabled));
  assert.equal(await reserveItems().count(), 2);
  assert.deepEqual(await reserveItems().locator(".memory-protection-path").allInnerTexts(), ["F:\\MockReserve", "G:\\MockReserve"]);
  assert.equal(await factText(reserveItems().nth(0), copy.reserveCluster), "4,096 B");
  assert.equal(await factText(reserveItems().nth(1), copy.reserveCluster), "65,536 B");
  assert.equal(await factText(reserveItems().nth(0), copy.reserveConfigured), "67.1 MB");
  assert.equal(await factText(reserveItems().nth(1), copy.reserveRemaining), "131 kB");
  assert.equal(await factText(totals(), copy.reserveTotals), "67.2 MB");
  assert.equal(await factText(totals(), copy.reserveBuildState), copy.reserveBuilt);
  await panel().getByRole("radio", { name: copy.actionModes.terminateOnly.label, exact: true }).click();
  await enable().check();
  assert.equal(await save(), 200);
  assert.deepEqual(patches.at(-1).changes, { memoryProtection: { enabled: true, actionMode: "terminateOnly" } });
  await page.waitForFunction(text => document.querySelector(".memory-protection-effective")?.textContent === text, copy.effective);
  const savedStatus = await (await fetch(`${baseUrl}/api/memory-protection`)).json();
  assert.equal(savedStatus.actionMode, "terminateOnly");
  assert.equal(savedStatus.enabled, true);

  // active：保护生效，触发点与剩余量照后端原值显示。
  await open("active");
  assert.equal(await effective(), copy.effective);
  await expectValues(sent.trigger, sent.remaining, sent.commit);
  assert.equal(await factText(totals(), copy.reserveRemaining), "67.2 MB");

  // terminate-only：全局处理方式为只结束。
  await open("terminate-only");
  assert.equal(await panel().getByRole("radio", { name: copy.actionModes.terminateOnly.label, exact: true }).getAttribute("aria-checked"), "true");
  assert.equal(await panel().getByRole("radio", { name: copy.actionModes.dumpThenTerminate.label, exact: true }).getAttribute("aria-checked"), "false");

  // reserve-building：未生效原因是保留区正在建立；显式重建后建成。
  await open("reserve-building");
  assert.equal(await effective(), copy.inactive(copy.reasons["reserve-building"]));
  assert.equal(await rebuild(), 200);
  await reserveStatus().getByText(copy.rebuildBuilt, { exact: true }).waitFor();
  await page.waitForFunction(text => document.querySelector(".memory-protection-effective")?.textContent === text, copy.effective);

  // reserve-failed：每处写出失败原因；重建返回 409，显示原因。
  await open("reserve-failed");
  const spaceReason = copy.reasons["insufficient-disk-space-for-reserve"];
  assert.equal(await factText(reserveItems().nth(0), copy.reserveBuildState), `${copy.reserveNotBuilt} · ${spaceReason}`);
  assert.equal(await effective(), copy.inactive(spaceReason));
  assert.equal(await rebuild(), 409);
  await reserveStatus().getByRole("alert").getByText(copy.rebuildNotBuilt(spaceReason), { exact: true }).waitFor();

  // reserve-partial-cluster：第二处不是整簇，原因写明 65536 字节的簇；保存非整簇大小被拒且不取整，改成整簇后建成。
  await open("reserve-partial-cluster");
  const clusterReason = copy.wholeClusters("65,536 B");
  assert.equal(await factText(reserveItems().nth(0), copy.reserveBuildState), copy.reserveBuilt);
  assert.equal(await factText(reserveItems().nth(1), copy.reserveBuildState), `${copy.reserveNotBuilt} · ${clusterReason}`);
  assert.equal(await factText(reserveItems().nth(1), copy.reserveCluster), "65,536 B");
  const secondSize = panel().getByRole("textbox", { name: `${copy.reserveEntryLabel(2)} · ${copy.reserveSize}`, exact: true });
  assert.equal(await secondSize.inputValue(), "131073");
  await secondSize.fill("131075");
  await secondSize.press("Enter");
  assert.equal(await save(), 409);
  assert.deepEqual(patches.at(-1).changes.memoryProtection.reserves.map(entry => entry.sizeBytes), [67108864, 131075],
    "the size is sent exactly as entered, not rounded to clusters");
  await panel().getByRole("alert").getByText(copy.rejected(clusterReason), { exact: true }).waitFor();
  // 新的保留区尚未建成时不能同时开启：先关闭保护再保存整簇大小。
  await secondSize.fill("196608");
  await secondSize.press("Enter");
  await enable().uncheck();
  assert.equal(await save(), 200);
  assert.deepEqual(patches.at(-1).changes.memoryProtection.reserves.map(entry => entry.sizeBytes), [67108864, 196608]);
  await page.waitForFunction(text => [...document.querySelectorAll(".memory-protection-reserve-status .memory-protection-item")][1]
    ?.querySelector(".memory-protection-built")?.textContent === text, copy.reserveBuilt);

  // reserve-incomplete：要求显式重建；重建后恢复。
  await open("reserve-incomplete");
  assert.equal(await effective(), copy.inactive(copy.reasons["reserve-build-incomplete; request an explicit rebuild"]));
  assert.equal(await rebuild(), 200);
  await page.waitForFunction(text => document.querySelector(".memory-protection-effective")?.textContent === text, copy.effective);

  // reserve-consumed：建成但剩余为 0，不自动补建；重建后恢复设定容量。
  await open("reserve-consumed");
  assert.equal(await factText(reserveItems().nth(0), copy.reserveRemaining), "0 B");
  assert.equal(await factText(totals(), copy.reserveRemaining), "0 B");
  assert.equal(await factText(totals(), copy.reserveBuildState), copy.reserveBuilt);
  assert.equal(await rebuild(), 200);
  await page.waitForFunction(() => document.querySelector(".memory-protection-totals")?.textContent?.includes("67.2 MB"));
  assert.equal(await factText(reserveItems().nth(0), copy.reserveRemaining), "67.1 MB");

  // commit-unavailable：当前提交量、触发点和剩余量都是 null，照原样说明后端未提供。
  await open("commit-unavailable");
  await expectValues(copy.valueNotProvided, copy.valueNotProvided, copy.valueNotProvided);

  // all-outcomes：七种结果、每种结果的原因码、诊断详情、跨卷有序的切片路径和排除原因都显示出来。
  await open("all-outcomes");
  const status = await (await fetch(`${baseUrl}/api/memory-protection`)).json();
  const records = panel().locator(".memory-protection-record");
  assert.equal(await records.count(), status.actions.length);
  // 原因是码，查表得文字；详情是后端的诊断原文：认得的写法（NTSTATUS、Win32 错误、未能解除提交）照文案写出，其余原样显示。
  const expectedDetails = {
    "release-limit": copy.releaseLimited("1", "4.00 KiB"),
    "preflight-failed": "ReadProcessMemory failed",
    "suspend-failed": copy.suspendStatus("0xc0000022"),
    "slice-incomplete": "fixture IO failure",
    "termination-failed": copy.terminateFailed("5"),
    "reserve-error": "Access to the reserve file was denied."
  };
  for (let index = 0; index < status.actions.length; index++) {
    const record = status.actions[index];
    const row = records.nth(index);
    assert.equal(await row.getAttribute("data-outcome"), record.outcome);
    assert.equal((await row.locator(".memory-protection-outcome").innerText()).trim(), copy.outcomes[record.outcome]);
    assert.equal((await row.locator(".memory-protection-item-head strong").innerText()).trim(), copy.recordProcess(record.name, record.processId));
    assert.equal(await factText(row, copy.recordGrowth), "700 B");
    assert.equal(await factText(row, copy.recordStartKey), record.processStartKey);
    if (record.reason === null) {
      assert.equal(await fact(row, copy.recordReason).count(), 0);
    } else {
      assert.equal(await factText(row, copy.recordReason), copy.reasons[record.reason]);
      assert.equal(await fact(row, copy.recordReason).getAttribute("data-full-content"), record.reason);
    }
    if (record.detail === null) {
      assert.equal(await fact(row, copy.recordDetail).count(), 0, `${record.outcome}: no detail`);
    } else {
      assert.equal(await factText(row, copy.recordDetail), expectedDetails[record.reason], `${record.reason}: detail`);
      assert.equal(await fact(row, copy.recordDetail).getAttribute("data-full-content"), record.detail, "hover shows the raw detail");
    }
    assert.equal(await row.locator("[title]").count(), 0, "records have no native tooltips");
    const paths = await row.locator(".memory-protection-paths li").allInnerTexts();
    assert.deepEqual(paths, record.dumpPaths, `${record.outcome}: dump paths keep the record order`);
    if (record.dumpPaths.length === 0) assert.equal(await factText(row, copy.recordDumpPaths), copy.recordNoDumpPaths);
  }
  assert.deepEqual(new Set(status.actions.map(record => record.outcome)).size, 7);
  const exclusions = panel().locator(".memory-protection-exclusion");
  assert.equal(await exclusions.count(), 10);
  for (let index = 0; index < status.excludedCandidates.length; index++) {
    const candidate = status.excludedCandidates[index];
    assert.equal((await exclusions.nth(index).locator("span").innerText()).trim(), copy.exclusionReasons[candidate.reason]);
  }

  // exclusions：只有排除的候选，没有处理记录。
  await open("exclusions");
  await panel().getByText(copy.recordsEmpty, { exact: true }).waitFor();
  assert.equal(await panel().locator(".memory-protection-exclusion").count(), 10);

  // tree-actions and action-in-progress belong to the memory-protection-events journey.
  assert.deepEqual([...new Set(covered)].sort(), memoryProtectionScenarios.filter(name => !["tree-actions", "action-in-progress"].includes(name)).sort(),
    "every settings mock scenario was opened");
  assert.equal(errors.length, 0, `page errors: ${errors.join("\n")}`);
  return { scenarios: [...new Set(covered)], rejectedEnables: ["no-normal-running-point", "reserve-not-configured"],
    exactReserveSizeSent: 131075, records: status.actions.length, exclusions: status.excludedCandidates.length };
}
