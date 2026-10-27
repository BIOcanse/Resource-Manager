import assert from "node:assert/strict";
import { createReadStream, mkdirSync } from "node:fs";
import { StringDecoder } from "node:string_decoder";
import { join } from "node:path";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 显卡编号旁边的名字（用户 10-27：「前端直接用后端读到的，不过由于有显卡名字小字所以不会错」）。
 * 编号是后端发的原样；名字取字段目录里同一设备身份的 gpu_adapter 自报的名字，监视卡片、资源条和它的芯片与来源、
 * 资源表的表头和列芯片都在编号旁用小字写出。夹具照真实切片 20261027T043049Z 的形状：gpu.0 是 NVIDIA、gpu.1 是 AMD 核显，
 * 没绑定字段的别名指标 deviceIdentity 为 null。设置 RM_REAL_SLICE 时目录来自切片，期望值也从服务端目录算。
 * RM_GPU_NAMES_SHOTS 给了目录时，zh-CN、en-US 各存一张页面截图（只截页面）。
 */
const nvidia = "windows-gpu|fixture-nvidia";
const amd = "windows-gpu|fixture-amd";
const gpuMetric = (id, unit, scopeKey, deviceIdentity = scopeKey) =>
  ({ id, unit, deviceIdentity, scopeKind: "gpu", scopeKey, selectable: true, candidateSources: [] });

export default async function gpuNamesJourney(page, baseUrl) {
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: {
    metricFieldDevices: [
      { deviceIdentity: amd, device: { kind: "gpu_adapter", address: null, displayName: "AMD Radeon(TM) 610M", ordinal: 1 }, fields: [] },
      { deviceIdentity: nvidia, device: { kind: "gpu_adapter", address: null, displayName: "NVIDIA GeForce RTX 5060 Laptop GPU", ordinal: 0 }, fields: [] }
    ],
    metricCatalogExtra: [
      gpuMetric("gpu.0.usage", "%", nvidia), gpuMetric("gpu.0.vram", "B", nvidia), gpuMetric("gpu.0.power", "W", nvidia, null),
      gpuMetric("gpu.1.usage", "%", amd), gpuMetric("gpu.1.vram", "B", amd, null)
    ]
  } });
  assert.equal(scenario.status(), 200);
  const expected = await expectedNames(page, baseUrl);
  assert.ok(expected.size >= 2, `Two named GPUs are needed: ${JSON.stringify([...expected])}`);
  const shots = process.env.RM_GPU_NAMES_SHOTS || null;
  const results = [];
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  for (const language of ["zh-CN", "en-US"]) {
    const app = await loadAppCopy(language);
    await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
    const result = { language };

    // 监视卡片：GPU 卡片的标签下面一行是名字。
    await page.waitForFunction(() => document.querySelectorAll(".metric-label .metric-device").length >= 2, undefined, { timeout: 15_000 });
    result.cards = await page.locator(".metric-label").evaluateAll(labels => labels
      .filter(label => label.querySelector(".metric-device"))
      .map(label => ({ label: label.querySelector(".metric-label-text").textContent.trim(), name: label.querySelector(".metric-device").textContent.trim() })));
    checkPairs("Dashboard card", result.cards, expected, app);

    // 资源条：条名下面一行是名字。
    const bars = page.locator(".resource-breakdown-panel");
    await bars.scrollIntoViewIfNeeded();
    await page.waitForFunction(() => document.querySelectorAll(".resource-breakdown-header strong .gpu-name-note").length >= 2, undefined, { timeout: 15_000 });
    result.bars = await bars.locator(".resource-breakdown-header strong").evaluateAll(items => items
      .filter(item => item.querySelector(".gpu-name-note"))
      .map(item => ({ label: item.firstChild.textContent.trim(), name: item.querySelector(".gpu-name-note").textContent.trim() })));
    checkPairs("Resource bar", result.bars, expected, app);

    // 资源表：GPU 列的表头里列名下面一行是名字，列名本身不变。
    const table = page.locator(".resource-table-panel");
    await table.scrollIntoViewIfNeeded();
    await page.waitForFunction(() => document.querySelectorAll('.resource-table-head-cell[data-column-id^="gpu."] .gpu-name-note').length >= 2, undefined, { timeout: 15_000 });
    result.headers = await table.locator('.resource-table-head-cell[data-column-id^="gpu."]').evaluateAll(cells => cells.map(cell => ({
      id: cell.dataset.columnId,
      label: cell.querySelector(".resource-table-sort-button > span").textContent.trim(),
      name: cell.querySelector(".gpu-name-note")?.textContent.trim() ?? null,
      fits: [...cell.querySelectorAll(".resource-table-sort-button > *")].every(child => {
        const box = child.getBoundingClientRect(), outer = cell.getBoundingClientRect();
        return box.top >= outer.top - 0.5 && box.bottom <= outer.bottom + 0.5;
      }) })));
    for (const header of result.headers) {
      const index = /^gpu\.(\d+)\./.exec(header.id)[1];
      assert.equal(header.name, expected.get(index) ?? null, `${language} header ${header.id}: ${JSON.stringify(header)}`);
      assert.ok(header.label.startsWith(gpuNumber(app, index)) && !header.label.includes(header.name ?? "\u0000"), `${language} header label ${JSON.stringify(header)}`);
      assert.ok(header.fits, `${language} header ${header.id} overflows its row: ${JSON.stringify(header)}`);
    }
    if (shots) {
      mkdirSync(shots, { recursive: true });
      await table.screenshot({ path: join(shots, `gpu-names-table-${language}.png`) });
      // 页面滚动在应用外壳里：回到顶部截卡片和资源条。
      await page.evaluate(() => document.querySelector("main.app-shell")?.scrollTo(0, 0));
      await page.waitForTimeout(300);
      await page.screenshot({ path: join(shots, `gpu-names-monitor-${language}.png`) });
    }

    // 资源条编辑：GPU 芯片和来源一行都写名字。
    await page.getByRole("button", { name: app.resourceBreakdownView.editLabel, exact: true }).click();
    const barEditor = page.getByRole("group", { name: app.resourceBreakdownView.editorLabel, exact: true });
    await barEditor.locator(".resource-bar-option .gpu-name-note").first().waitFor({ timeout: 10_000 });
    result.barChips = await barEditor.locator(".resource-bar-select").evaluateAll(buttons => buttons
      .filter(button => button.querySelector(".gpu-name-note"))
      .map(button => ({ label: button.firstChild.textContent.trim(), name: button.querySelector(".gpu-name-note").textContent.trim() })));
    checkPairs("Resource bar chip", result.barChips, expected, app);
    if (shots) await barEditor.screenshot({ path: join(shots, `gpu-names-bar-editor-${language}.png`) });
    await page.getByRole("button", { name: app.resourceBreakdownView.cancelEditLabel, exact: true }).click();

    // 资源表编辑：GPU 列芯片写名字。
    await table.scrollIntoViewIfNeeded();
    await table.locator('[data-focus-key="resource-table-edit"]').click();
    const columnEditor = table.getByRole("group", { name: app.resourceTableView.columnEditorLabel, exact: true });
    await columnEditor.locator(".resource-table-column-option .gpu-name-note").first().waitFor({ timeout: 10_000 });
    result.columnChips = await columnEditor.locator(".resource-table-column-select").evaluateAll(buttons => buttons
      .filter(button => button.querySelector(".gpu-name-note"))
      .map(button => ({ label: button.firstChild.textContent.trim(), name: button.querySelector(".gpu-name-note").textContent.trim() })));
    checkPairs("Resource table column chip", result.columnChips, expected, app);
    if (shots) await columnEditor.screenshot({ path: join(shots, `gpu-names-column-editor-${language}.png`) });
    await table.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).click();
    results.push(result);
  }
  await writeFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  return { expected: [...expected], results };
}

/** 编号的写法照文案：GPU 编号是后端发的原样（gpu.1 写 GPU1）。 */
function gpuNumber(app, index) {
  return app.resourceTable.gpuUsageLabel(index).split(/\s/)[0];
}

/** 每一项的标签以 GPU 编号开头，名字正是这块显卡的名字；两块显卡都要出现。 */
function checkPairs(what, pairs, expected, app) {
  const seen = new Set();
  for (const pair of pairs) {
    const index = [...expected.keys()].find(key => pair.label.startsWith(gpuNumber(app, key)) && !/\d/.test(pair.label.slice(gpuNumber(app, key).length, gpuNumber(app, key).length + 1)));
    assert.ok(index !== undefined, `${what}: a name without a GPU number: ${JSON.stringify(pair)}`);
    assert.equal(pair.name, expected.get(index), `${what}: ${JSON.stringify(pair)}`);
    seen.add(index);
  }
  assert.deepEqual([...seen].sort(), [...expected.keys()].sort(), `${what}: every named GPU appears: ${JSON.stringify(pairs)}`);
}

/**
 * 期望值从页面收到的同一份目录算：gpu.N 指标的 scopeKey 对上 gpu_adapter 设备自报的名字。
 * 夹具：mock 的目录 GET（由场景生成，和推送信封同源）。真实切片：切片里第一帧字段目录推送信封，它同时带 fields 和 metrics
 * （mock 回放的就是它；GET /api/metrics/fields/catalog 不在切片里，仍是夹具），按流读，不把整份切片读进内存。
 */
async function expectedNames(page, baseUrl) {
  const envelope = process.env.RM_REAL_SLICE ? await firstSliceCatalogEnvelope(process.env.RM_REAL_SLICE) : {
    metrics: await (await page.request.get(`${baseUrl}/api/metrics/catalog`)).json(),
    fields: await (await page.request.get(`${baseUrl}/api/metrics/fields/catalog`)).json()
  };
  const nameByIdentity = new Map(envelope.fields.filter(item => item.device?.kind === "gpu_adapter" && item.device.displayName?.trim())
    .map(item => [item.deviceIdentity, item.device.displayName.trim()]));
  const names = new Map();
  for (const metric of envelope.metrics) {
    const index = /^gpu\.(\d+)\./.exec(metric.id)?.[1];
    const name = index !== undefined && metric.scopeKind === "gpu" ? nameByIdentity.get(metric.scopeKey) : undefined;
    if (name) names.set(index, name);
  }
  return names;
}

/** 切片 slice.json 里第一帧 /api/metrics/fields/catalog/subscribe 的 body：找到标记后按括号配对取出这一段 JSON（跳过字符串里的括号）。 */
async function firstSliceCatalogEnvelope(sliceDirectory) {
  const marker = '"path":"/api/metrics/fields/catalog/subscribe';
  const decoder = new StringDecoder("utf8");
  let text = "";
  let bodyStart = -1;
  for await (const chunk of createReadStream(join(sliceDirectory, "slice.json"), { highWaterMark: 1 << 20 })) {
    text += decoder.write(chunk);
    if (bodyStart < 0) {
      const at = text.indexOf(marker);
      if (at < 0) { text = text.slice(-marker.length); continue; }
      const body = text.indexOf('"body":', at);
      if (body < 0) { text = text.slice(at); continue; }
      text = text.slice(body + '"body":'.length);
      bodyStart = 0;
    }
    let depth = 0, inString = false, escaped = false;
    for (let index = 0; index < text.length; index += 1) {
      const character = text[index];
      if (inString) {
        if (escaped) escaped = false;
        else if (character === "\\") escaped = true;
        else if (character === '"') inString = false;
      } else if (character === '"') inString = true;
      else if (character === "{") depth += 1;
      else if (character === "}" && --depth === 0) return JSON.parse(text.slice(0, index + 1));
    }
  }
  throw new Error(`No field catalog frame in ${sliceDirectory}`);
}
