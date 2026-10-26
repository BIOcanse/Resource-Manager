import assert from "node:assert/strict";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function productUpdatesJourney(page, baseUrl) {
  const copy = productUpdateCopy("zh-CN");
  const text = (await loadSettingsText("zh-CN")).updates;
  let statusReads = 0;
  let applyCalls = 0;
  const patches = [];
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("dialog", dialog => dialog.accept());
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "PATCH" && path === "/api/settings/app") patches.push(request.postDataJSON());
    if (path === "/api/updates/product/status") statusReads++;
  });
  // 更新状态只来自统一订阅通道的推送；"invalid" 让推送帧不合合同，界面按状态不可用处理。
  const publish = async status => assert.ok((await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ productUpdateStatus: status }) })).ok);
  const ready = { stage: "pendingApply", targetVersion: "1.1.0", checkResult: "available", availableVersion: "1.1.0" };
  // 两个系列，没有稳定版；1.0 系列的两版都不高于已安装版本。
  const history = [["1.2.0", "1.2", "eligible"], ["1.1.0", "1.1", "eligible"], ["1.0.0", "1.0", "notNewer"], ["1.0.0-beta.2", "1.0", "notNewer"]]
    .map(([version, series, eligibility]) => ({ choice: `version:${version}`, version, series, channel: "preview",
      available: true, selectable: eligibility === "eligible", eligibility }));
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "1.0.0", status: "loaded", complete: true, checkedAt: new Date().toISOString(),
    options: [{ choice: "latest", version: "1.2.0", series: "1.2", channel: "preview", selectable: true, available: true }, ...history]
  } }));
  await page.route("**/api/updates/product/apply", route => {
    assert.equal(route.request().method(), "POST");
    applyCalls++;
    return route.fulfill({ status: 202, json: { stage: "waitingForExit", targetVersion: "1.1.0" } });
  });
  await publish("invalid");
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "更新与版本", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: copy.unavailable }).waitFor();
  // 更新区自己的“立即应用”；同一版本的界面内提示也带一个，由 update-notice 旅程覆盖。
  const region = page.locator(".settings-content");
  assert.equal(await region.getByRole("button", { name: copy.apply, exact: true }).count(), 0);
  await publish(ready);
  const apply = region.getByRole("button", { name: copy.apply, exact: true });
  await apply.waitFor({ timeout: 7000 });
  // 1.1.0 是新版本，界面内提示一直显示到用户关闭；它停在页面右上角，先关掉再操作设置页头部的保存。
  const notice = page.locator(".toast").filter({ hasText: text.noticeTitle });
  await notice.getByRole("button", { name: "关闭", exact: true }).click();
  await page.getByText("已选择目标版本：1.1.0", { exact: true }).waitFor();
  const versionList = [await checkVersionList(page, region, text, "desktop")];
  await setAnimation(page, "none");
  versionList.push(await checkVersionList(page, region, text, "none"));
  await setAnimation(page, "desktop");
  await page.locator('input[value="latest"]').check();
  assert.equal(await page.getByRole("button", { name: "准备所选更新", exact: true }).isDisabled(), true,
    "A pending package must prevent preparing a replacement while Apply now remains available");
  assert.equal(await apply.isEnabled(), true);
  await publish("invalid");
  await page.getByRole("alert").filter({ hasText: copy.stale }).waitFor({ timeout: 7000 });
  assert.equal(await apply.count(), 0, "A stale package status still exposed Apply now");
  await publish(ready);
  await apply.waitFor({ timeout: 7000 });
  const interval = page.getByRole("textbox", { name: text.intervalTitle, exact: true });
  assert.equal(await interval.inputValue(), "24");
  await page.getByRole("checkbox", { name: text.periodicTitle, exact: true }).check();
  for (const hours of [72, 24]) {
    await interval.fill(String(hours));
    await interval.blur();
    const response = page.waitForResponse(response => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: "保存", exact: true }).click();
    assert.ok((await response).ok());
    assert.equal(patches.at(-1)?.changes?.update?.checkIntervalHours, hours);
  }
  await page.evaluate(() => {
    window.__updateHostMessages = [];
    window.unboundWebView = { postMessage: message => window.__updateHostMessages.push(message) };
  });
  await apply.click();
  await page.waitForFunction(() => window.__updateHostMessages.includes("update.applyExit"));
  assert.equal(applyCalls, 1);
  assert.equal(statusReads, 0, "The update region polled the status instead of using the push");
  assert.deepEqual(errors, []);
  return { statusReads, applyCalls, intervalSaves: patches.filter(patch => patch.changes?.update).map(patch => patch.changes.update.checkIntervalHours), versionList, recoveredInitialFailure: true, recoveredStaleStatus: true };
}

/** 设置 → 外观 → 动画风格，保存后回到“更新与版本”。 */
async function setAnimation(page, id) {
  const settings = await loadSettingsText("zh-CN");
  await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
  const label = settings.animationStyleOptions.find(option => option.id === id).label;
  await page.getByRole("radiogroup", { name: settings.appearance.animationStyleTitle, exact: true }).getByRole("radio", { name: label, exact: true }).click();
  // 动画风格是前端偏好：保存后直接写进前端偏好缓存，不发后端设置请求。
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.waitForFunction(mode => document.body.dataset.animations === mode, id, { timeout: 5000 });
  await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
  await page.locator(".version-selector").waitFor();
}

/**
 * 版本列表（VersionSelector.tsx）：不能选的原因只在顶上说一次，稳定版的空状态在固定项的格子里，
 * 系列标题是低调的折叠标题（箭头在左，不是实心按钮），收起时内容 inert；正常档展开有中间帧，无动画档直接到终态；单选圆点和标题对齐。
 */
async function checkVersionList(page, region, text, mode) {
  const list = region.locator(".version-selector");
  await list.waitFor();
  assert.equal(await region.getByText(text.unavailableReasons.notNewer, { exact: true }).count(), 1, "The not-newer reason must be said once");
  const facts = await list.evaluate((element) => {
    const note = element.querySelector(".version-selector-notes li");
    const empty = element.querySelector(".version-selector-pinned > .version-selector-empty");
    const headers = Array.from(element.querySelectorAll(".version-selector-expand"));
    const disabled = Array.from(element.querySelectorAll('input[type="radio"]:disabled'));
    const rows = Array.from(element.querySelectorAll(".version-selector-item"));
    return {
      noteId: note?.id,
      noteText: note?.textContent,
      emptyText: empty?.textContent,
      headers: headers.map((header) => {
        const chevron = header.querySelector(".ui-disclosure-chevron").getBoundingClientRect();
        const label = header.querySelector(".ui-disclosure-label").getBoundingClientRect();
        return { text: header.textContent.trim(), expanded: header.getAttribute("aria-expanded"),
          background: getComputedStyle(header).backgroundColor, tag: header.tagName, chevronLeft: chevron.right <= label.left };
      }),
      describedBy: [...new Set(disabled.map((input) => input.getAttribute("aria-describedby")))],
      rowsWithReason: note ? rows.filter((row) => row.textContent.includes(note.textContent)).length : -1,
      alignment: rows.filter((row) => row.getBoundingClientRect().height > 0).map((row) => {
        const radio = (row.querySelector('input[type="radio"]') ?? row.querySelector(".version-selector-radio-space")).getBoundingClientRect();
        const range = document.createRange();
        range.selectNodeContents(row.querySelector("strong"));
        const firstLine = range.getClientRects()[0];
        return Math.abs((radio.top + radio.height / 2) - (firstLine.top + firstLine.height / 2));
      })
    };
  });
  assert.ok(facts.emptyText?.includes(text.latestStable) && facts.emptyText.includes(text.noStable),
    `The missing stable version must be an empty slot in the pinned list: ${facts.emptyText}`);
  assert.deepEqual(facts.headers.map(header => header.text), ["1.0"], "Only a series with more than one version has a disclosure header");
  const [header] = facts.headers;
  assert.ok(header.tag === "BUTTON" && header.expanded === "false" && header.chevronLeft, `Series header: ${JSON.stringify(header)}`);
  assert.equal(header.background, "rgba(0, 0, 0, 0)", "The series header must not be a solid button");
  assert.equal(facts.noteText, text.unavailableReasons.notNewer);
  assert.deepEqual(facts.describedBy, [facts.noteId], "Disabled versions must point at the note instead of repeating it");
  assert.equal(facts.rowsWithReason, 0, "A version row repeats the reason");
  assert.ok(facts.alignment.length >= 4 && facts.alignment.every(offset => offset <= 1.5), `Radio and title are not aligned: ${facts.alignment}`);

  const older = list.locator('input[value="version:1.0.0-beta.2"]');
  const inert = () => older.evaluate(input => input.closest("[inert]") !== null);
  assert.ok(await inert(), "A collapsed series must keep its older versions inert");
  const toggle = () => page.evaluate(async () => {
    const button = document.querySelector(".version-selector-expand");
    const body = button.parentElement.querySelector(".ui-disclosure-body");
    const heights = [];
    button.click();
    const start = performance.now();
    while (performance.now() - start < 700) {
      await new Promise(resolve => requestAnimationFrame(resolve));
      heights.push(Math.round(body.getBoundingClientRect().height));
    }
    return { heights, expanded: button.getAttribute("aria-expanded"), openness: button.style.getPropertyValue("--disclosure-openness").trim() };
  });
  const opened = await toggle();
  const full = opened.heights.at(-1);
  const between = heights => heights.filter(height => height > 1 && height < full - 1).length;
  assert.ok(full > 20 && opened.expanded === "true" && Number(opened.openness) === 1 && !await inert(), `Series did not expand: ${JSON.stringify(opened)}`);
  if (mode === "desktop") assert.ok(between(opened.heights) >= 3, `Normal motion: expanding has no in-between frames: ${opened.heights}`);
  else assert.ok(opened.heights[1] === full && between(opened.heights.slice(1)) === 0, `No motion: expanding still animates: ${opened.heights.slice(0, 6)}`);
  assert.ok(await older.isDisabled(), "An older version can be selected");
  const closed = await toggle();
  assert.ok(closed.heights.at(-1) === 0 && closed.expanded === "false" && Number(closed.openness) === 0 && await inert(),
    `Series did not collapse: ${JSON.stringify(closed)}`);
  if (mode === "desktop") assert.ok(between(closed.heights) >= 3, `Normal motion: collapsing has no in-between frames: ${closed.heights}`);
  else assert.ok(closed.heights[1] === 0, `No motion: collapsing still animates: ${closed.heights.slice(0, 6)}`);
  return { mode, openFrames: opened.heights.length, full };
}
