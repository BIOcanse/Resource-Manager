import assert from "node:assert/strict";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 资源条区的加载占位和有数据时一样高（用户 10-27：「让占位和加载后状态的逻辑占位大小相等」）：
 * - 第一批资源条数据到之前，设置里的每一条各占一个位置：条名、“--”、刻度和一条空轨道；
 * - 数据到了，占位换成资源条，整个资源条区的高度、每一条的高度都不变，下面的资源表不被推动；
 * - 正在加载照旧读给读屏（role=status）。
 * 资源条的订阅先拦住，量完占位再放行，所以“还没到”是确定的，不靠时间。宽窗口和最小窗口、中英两种语言各走一遍。
 */
// 所有订阅走同一条 POST /api/subscriptions/stream；请求体里有资源条那一路的，整条先拦住。
const streamUrl = (url) => url.pathname === "/api/subscriptions/stream";
const carriesBars = (request) => request.method() === "POST" && (request.postData() ?? "").includes("scope=bars");

async function heights(page) {
  return page.evaluate(() => {
    const region = document.querySelector(".resource-breakdown");
    const table = document.querySelector(".resource-table-panel");
    const rows = [...document.querySelectorAll(".resource-breakdown > article")];
    return {
      region: region ? region.getBoundingClientRect().height : null,
      tableTop: table ? table.getBoundingClientRect().top + window.scrollY : null,
      rows: rows.map((row) => ({ kind: row.className, height: row.getBoundingClientRect().height }))
    };
  });
}

export default async function resourceBarPlaceholderJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));
  await seedFrontendPreferences(page, (value) => {
    value.appearance.language = "zh-CN";
    value.appearance.animationStyle = "none";
  });
  const results = [];
  for (const { language, width, height } of [
    { language: "zh-CN", width: 1280, height: 900 },
    { language: "en-US", width: 744, height: 520 }
  ]) {
    let release;
    const held = new Promise((resolve) => { release = resolve; });
    const hold = async (route) => {
      if (!carriesBars(route.request())) return route.continue();
      await held;
      await route.continue().catch(() => {});
    };
    await page.route(streamUrl, hold);
    await page.setViewportSize({ width, height });
    // 第一轮的语言由 seed 写入；之后页面已在本站，直接改偏好。
    if (results.length > 0) await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.locator(".resource-breakdown-panel").scrollIntoViewIfNeeded();
    await page.locator(".resource-breakdown-slot").first().waitFor({ state: "visible", timeout: 8000 })
      .catch(() => assert.fail(`${language}: no loading placeholder slots while the bars are held`));
    assert.ok(await page.locator(".resource-breakdown [role=status]").count() === 1, `${language}: loading is announced`);
    const before = await heights(page);

    release();
    await page.unroute(streamUrl, hold);
    await page.waitForFunction((count) => document.querySelectorAll(".resource-breakdown-item").length === count
      && document.querySelector(".resource-breakdown-slot") === null, before.rows.length, { timeout: 8000 })
      .catch(async () => assert.fail(`${language}: the placeholder slots are not replaced one for one ${JSON.stringify({ before, after: await heights(page) })}`));
    const after = await heights(page);

    assert.ok(Math.abs(after.region - before.region) <= 0.5, `${language} ${width}: the bars region changes height when data arrives ${JSON.stringify({ before, after })}`);
    before.rows.forEach((row, index) => {
      assert.ok(Math.abs(after.rows[index].height - row.height) <= 0.5, `${language} ${width}: bar ${index + 1} changes height ${JSON.stringify({ before, after })}`);
    });
    assert.ok(Math.abs(after.tableTop - before.tableTop) <= 0.5, `${language} ${width}: the resource table moves when the bars arrive ${JSON.stringify({ before, after })}`);
    results.push({ language, width, bars: before.rows.length, region: before.region });
  }
  assert.deepEqual(failures, [], "no page errors");
  return { results };
}
