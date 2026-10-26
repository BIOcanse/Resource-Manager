import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { metricFieldDevices, npuBusyCardId, readingSourceDevices, readingSourceIds } from "./metricFieldsProbe.js";

/**
 * 监视卡片的读数状态和排版：
 * 1. 首个读数到之前画“读取中”占位（不先写“无读数”再跳成数值），占位和数值同高，数值淡入；设备不在照常直接写；
 * 2. 德文窄卡片上小行标签最多两行、不截成省略号，没有值时那句话靠右折行、不挤掉标签；
 * 3. 四种风格里数值和单位之间的空格按单位字号排（终端风格的等宽大字号下不宽出一截）；
 * 4. 强制配色下读数来源标记有边框、没有值的读数用系统灰字；
 * 5. 编辑时可拖的槽和卡片空白处各自有悬停反馈，槽内按钮淡入；无动画档下这些过渡和数值淡入都是 0。
 */
export default async function metricCardStatesJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const patch = async (body) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
    });
    assert.ok(response.ok, `scenario ${response.status}`);
  };
  const bind = (id) => ({ scopeKind: "device-field", scopeKey: id.slice(id.indexOf("@") + 1) });
  const cards = [
    { id: "cpu", main: "cpu.usage", small: ["cpu.frequency", "cpu.frequencyPercent"] },
    { id: "fields", main: npuBusyCardId, mainBinding: bind(npuBusyCardId),
      small: ["npu_all_engine_busy_percent@npu|removed", "cpu_intel_effective_frequency_mhz@intel-cpu|t1"],
      smallBindings: [bind("x@npu|removed"), bind("x@intel-cpu|t1")] },
    { id: "sources", main: readingSourceIds.preferred, mainBinding: bind(readingSourceIds.preferred),
      small: [readingSourceIds.pending], smallBindings: [bind(readingSourceIds.pending)] }
  ];
  const tableColumns = [{ id: "name", visible: true, width: 260 }, { id: "cpu", visible: true, width: 110 }];
  const preferences = pointPreferences();
  preferences.appearance.uiStyle = "standard";
  preferences.appearance.theme = "light";
  preferences.dashboard = { ...preferences.dashboard, version: 16, cards, resourceBars: [],
    resourceTableColumns: tableColumns, resourceTableProcessColumns: tableColumns };
  // 起始偏好只写一次；之后按用户保存的结果改缓存再 reload。每次加载都从头记下卡片上每个读数显示过什么。
  await page.addInitScript(([key, text]) => {
    if (location.origin === "null") return;
    if (!sessionStorage.getItem("metric-card-states-seeded")) {
      localStorage.setItem(key, text);
      sessionStorage.setItem("metric-card-states-seeded", "yes");
    }
    const shown = [];
    window.__metricCardValues = shown;
    new MutationObserver(() => {
      const values = [...document.querySelectorAll(".dashboard .metric-value")].map((value) =>
        value.classList.contains("pending") ? "<pending>" : value.textContent);
      const signature = values.join(" | ");
      if (shown.at(-1) !== signature) shown.push(signature);
    }).observe(document, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["class"] });
  }, [frontendPreferencesKey, JSON.stringify(preferences)]);

  const zh = await loadAppCopy("zh-CN");
  const card = (id) => page.locator(`.metric-card[data-card-id="${id}"]`);
  const openMonitor = async (app) => {
    await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
    await card("cpu").waitFor();
  };

  // 1. 首个读数：指标推送暂停时只有目录到了。
  await patch({ metricFieldDevices: [...metricFieldDevices, ...readingSourceDevices], pausedSubscriptionPaths: ["/api/metrics/subscribe"] });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await openMonitor(zh);
  // 字段目录 5 秒推一次：目录到了，不在目录里的设备照常写「设备不在」，不当作在等读数。
  await card("fields").locator(".metric-small-row").first().locator(".metric-value")
    .filter({ hasText: zh.dashboard.deviceAbsent }).waitFor({ timeout: 15_000 });
  const pending = page.locator(".dashboard .metric-value.pending");
  assert.equal(await pending.count(), 7, "every slot without a delivered value shows the waiting placeholder");
  for (const element of await pending.all()) {
    assert.equal(await element.getAttribute("title"), zh.dashboard.waitingReading);
    assert.equal(await element.locator(".sr-only").textContent(), zh.dashboard.waitingReading);
    assert.equal(await element.locator(".metric-pending-bar").evaluate((bar) => getComputedStyle(bar).animationName), "motion-pending-pulse");
  }
  const boxHeight = (locator) => locator.evaluate((element) => element.getBoundingClientRect().height);
  const pendingMain = await boxHeight(card("cpu").locator(".metric-main .metric-value"));
  const pendingRow = await boxHeight(card("cpu").locator(".metric-small-row").first());

  await patch({ pausedSubscriptionPaths: [] });
  await card("cpu").locator(".metric-main .metric-value").filter({ hasText: "12%" }).waitFor({ timeout: 15_000 });
  await card("fields").locator(".metric-main .metric-value").filter({ hasText: "137.5%" }).waitFor({ timeout: 15_000 });
  await card("fields").locator(".metric-small-row").nth(1).locator(".metric-value")
    .filter({ hasText: zh.dashboard.valueUnavailable }).waitFor({ timeout: 15_000 });
  await card("sources").locator(".metric-main .metric-value").filter({ hasText: "°C" }).waitFor({ timeout: 15_000 });
  assert.equal(await pending.count(), 0);
  assert.ok(Math.abs(await boxHeight(card("cpu").locator(".metric-main .metric-value")) - pendingMain) < 0.5,
    "the main value takes the placeholder's height: the card does not move when the reading arrives");
  assert.ok(Math.abs(await boxHeight(card("cpu").locator(".metric-small-row").first()) - pendingRow) < 0.5,
    "a small row keeps its height when its reading arrives");
  assert.equal(await card("cpu").locator(".metric-main .metric-value").evaluate((value) => getComputedStyle(value).animationName), "motion-fade-in");
  const shown = await page.evaluate(() => window.__metricCardValues);
  assert.ok(shown.length > 1, "the value timeline was recorded");
  for (const signature of shown) {
    assert.ok(!signature.split(" | ").includes(zh.dashboard.noReading), `a slot claimed "no reading" before its first reading: ${signature}`);
    assert.ok(!signature.split(" | ").includes("--"), `a slot showed "--" before its first reading: ${signature}`);
  }

  // 5. 编辑：槽和卡片空白处的悬停反馈，按钮淡入。
  await page.getByRole("button", { name: zh.monitorPage.editLayoutLabel, exact: true }).click();
  const slots = card("cpu").locator(".edit-slot.has-metric");
  await slots.nth(1).waitFor();
  const borderOf = (locator) => locator.evaluate((element) => getComputedStyle(element).borderTopColor);
  const restingSlot = await borderOf(slots.nth(2));
  const restingCard = await borderOf(card("cpu"));
  await slots.nth(1).hover();
  await page.waitForTimeout(400);
  assert.notEqual(await borderOf(slots.nth(1)), restingSlot, "a draggable slot lights up under the pointer");
  assert.equal(await borderOf(card("cpu")), restingCard, "the card does not light up while a slot is the drag target");
  assert.equal(await slots.nth(1).locator(".slot-actions").evaluate((actions) => getComputedStyle(actions).opacity), "1");
  const cardBox = await card("cpu").boundingBox();
  await page.mouse.move(cardBox.x + cardBox.width - 6, cardBox.y + cardBox.height - 4);
  await page.waitForTimeout(400);
  assert.notEqual(await borderOf(card("cpu")), restingCard, "the card lights up when pressing would drag the whole card");
  assert.match(await slots.nth(1).locator(".slot-actions").evaluate((actions) => getComputedStyle(actions).transitionProperty), /opacity/);
  await page.evaluate(() => { document.body.dataset.animations = "none"; });
  assert.equal(await slots.nth(1).locator(".slot-actions").evaluate((actions) => getComputedStyle(actions).transitionDuration), "0s");
  assert.equal(await card("cpu").locator(".metric-main .metric-value").evaluate((value) => getComputedStyle(value).animationDuration), "0s");
  await page.getByRole("button", { name: zh.monitorPage.cancelEditLabel, exact: true }).click();

  // 4. 强制配色。
  await page.emulateMedia({ forcedColors: "active" });
  const mark = card("sources").locator(".reading-source-mark").first();
  assert.equal(await mark.evaluate((element) => getComputedStyle(element).borderTopStyle), "solid", "the reading-source mark keeps an outline in forced colours");
  const grayText = await page.evaluate(() => {
    const probe = document.createElement("span");
    probe.style.cssText = "forced-color-adjust:none;color:GrayText";
    document.body.append(probe);
    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
  });
  assert.equal(await card("fields").locator(".metric-value.missing").first().evaluate((value) => getComputedStyle(value).color), grayText,
    "a value without a reading uses the system grey text in forced colours");
  await page.emulateMedia({ forcedColors: "none" });

  // 2. 德文窄卡片：小行标签和没有值时的那句话都完整显示。
  const de = await loadAppCopy("de-DE");
  await writeFrontendPreferences(page, (value) => { value.appearance.language = "de-DE"; value.appearance.uiStyle = "standard"; value.appearance.theme = "light"; });
  await page.setViewportSize({ width: 560, height: 900 });
  await page.reload({ waitUntil: "domcontentloaded" });
  await openMonitor(de);
  const absentRow = card("fields").locator(".metric-small-row").first();
  await absentRow.locator(".metric-value").filter({ hasText: de.dashboard.deviceAbsent }).waitFor({ timeout: 15_000 });
  await card("fields").locator(".metric-small-row").nth(1).locator(".metric-value").filter({ hasText: de.dashboard.valueUnavailable }).waitFor({ timeout: 15_000 });
  const wrapping = [];
  for (const row of await card("fields").locator(".metric-small-row").all()) {
    wrapping.push(await row.evaluate((element) => {
      const fits = (node) => node.scrollWidth <= node.clientWidth + 1 && node.scrollHeight <= node.clientHeight + 1;
      const label = element.querySelector(".metric-label-text");
      const value = element.querySelector(".metric-value");
      return { label: label.textContent, labelFits: fits(label), value: value.textContent, valueFits: fits(value),
        cardWidth: element.closest(".metric-card").getBoundingClientRect().width };
    }));
  }
  for (const row of wrapping) {
    assert.ok(row.cardWidth < 330, "the narrow card uses the one-line small rows");
    assert.ok(row.labelFits, `label "${row.label}" is cut off next to "${row.value}"`);
    assert.ok(row.valueFits, `value "${row.value}" is cut off`);
  }

  // 3. 数值和单位之间的空格：四种风格都在数值字号的 0.1–0.4 倍之间。
  const gapRatio = () => card("sources").locator(".metric-main .metric-value").evaluate((value) => {
    const number = value.querySelector(".metric-number");
    const unitText = value.querySelector(".metric-unit").firstChild;
    const range = document.createRange();
    range.setStart(unitText, unitText.textContent.length - unitText.textContent.trimStart().length);
    range.setEnd(unitText, unitText.textContent.length);
    return (range.getBoundingClientRect().left - number.getBoundingClientRect().right) / parseFloat(getComputedStyle(number).fontSize);
  });
  // 最后回到中文、宽窗口：运行器收尾的可用性检查按中文区域名找资源列表。
  await page.setViewportSize({ width: 1280, height: 900 });
  const gaps = {};
  for (const [uiStyle, theme] of [["standard", "light"], ["soft", "light"], ["outline", "paper"], ["terminal", "phosphor"]]) {
    await writeFrontendPreferences(page, (value) => { value.appearance.language = "zh-CN"; value.appearance.uiStyle = uiStyle; value.appearance.theme = theme; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await openMonitor(zh);
    await card("sources").locator(".metric-main .metric-value").filter({ hasText: "°C" }).waitFor({ timeout: 15_000 });
    gaps[uiStyle] = Number((await gapRatio()).toFixed(3));
    assert.ok(gaps[uiStyle] > 0.1 && gaps[uiStyle] < 0.4, `${uiStyle}: number-unit gap is ${gaps[uiStyle]} em`);
  }

  assert.deepEqual(errors, []);
  return { pendingSlots: 7, timelineSteps: shown.length, gaps, wrapping };
}
