// 资源条截图审查：起 mock 前端服务器，按界面风格、配色、视口、语言、数据前后截资源条区域（只截页面元素），
// 另截悬停提示、键盘焦点、强制颜色（高对比）、异色模式，并逐帧记录一次数据更新时各段边界的位置。
// 不进浏览器门；改资源条的呈现时用它做前后对比。
//   node tests/browser/resourceBarAuditShots.mjs --static <构建目录> --out <截图目录> [--only <名字片段>]
// 需要 RM_PLAYWRIGHT_MODULE（或项目内的 playwright）和 RM_PLAYWRIGHT_BROWSER（或 Playwright 自带浏览器）。
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { frontendPreferencesKey } from "./frontendPreferencesFixture.js";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const args = parseArgs(process.argv.slice(2));
const staticRoot = resolve(required(args, "static"));
const outDir = resolve(required(args, "out"));
const only = args.only ?? "";
mkdirSync(outDir, { recursive: true });

const styles = [
  { uiStyle: "standard", light: "light", dark: "dark" },
  { uiStyle: "outline", light: "paper", dark: "graphite" },
  { uiStyle: "soft", light: "light", dark: "dark" },
  // 终端风格只有深色配色：“浅色”位用 amber，深色位用 phosphor。
  { uiStyle: "terminal", light: "amber", dark: "phosphor" }
];
const viewports = { narrow: { width: 720, height: 900 }, wide: { width: 1440, height: 900 } };
const languages = ["zh-CN", "de-DE"];

const port = await reservePort();
const baseUrl = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, ["--experimental-transform-types", resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
  cwd: projectRoot,
  env: { ...process.env, RM_FRONTEND_HARNESS_PORT: String(port), RM_FRONTEND_STATIC_ROOT: staticRoot },
  stdio: ["ignore", "pipe", "pipe"]
});
let serverOutput = "";
server.stdout.on("data", chunk => { serverOutput += chunk; });
server.stderr.on("data", chunk => { serverOutput += chunk; });

const manifest = [];
let browser;
try {
  await waitForServer();
  const { chromium } = await import(pathToFileURL(playwrightModule()).href);
  browser = await chromium.launch({ headless: true, ...(browserExecutable() ? { executablePath: browserExecutable() } : {}) });

  for (const style of styles) {
    for (const tone of ["light", "dark"]) {
      for (const [viewportName, viewport] of Object.entries(viewports)) {
        for (const language of languages) {
          const name = `${style.uiStyle}-${style[tone]}-${viewportName}-${language}`;
          if (only && !name.includes(only)) continue;
          await withPage({ uiStyle: style.uiStyle, theme: style[tone], language, viewport }, async page => {
            await shot(page, `${name}-g0`);
            await advanceGeneration(page, 1);
            await shot(page, `${name}-g1`);
          });
        }
      }
    }
  }

  const extras = [
    ["standard-lowContrast-wide-zh-CN", { uiStyle: "standard", theme: "lowContrast" }],
    ["standard-light-wide-zh-CN-distinct", { uiStyle: "standard", theme: "light", barColorMode: "distinct" }],
    ["standard-dark-wide-zh-CN-distinct", { uiStyle: "standard", theme: "dark", barColorMode: "distinct" }],
    ["standard-light-wide-zh-CN-forced", { uiStyle: "standard", theme: "light", forcedColors: "active" }],
    ["standard-dark-wide-zh-CN-forced", { uiStyle: "standard", theme: "dark", forcedColors: "active", colorScheme: "dark" }]
  ];
  for (const [name, options] of extras) {
    if (only && !name.includes(only)) continue;
    await withPage({ language: "zh-CN", viewport: viewports.wide, ...options }, async page => {
      await shot(page, name);
      await focusShots(page, name);
    });
  }

  for (const [name, options] of [
    ["interaction-standard-light-wide-zh-CN", { uiStyle: "standard", theme: "light", language: "zh-CN", viewport: viewports.wide }],
    ["interaction-standard-dark-narrow-de-DE", { uiStyle: "standard", theme: "dark", language: "de-DE", viewport: viewports.narrow }],
    ["interaction-terminal-phosphor-wide-zh-CN", { uiStyle: "terminal", theme: "phosphor", language: "zh-CN", viewport: viewports.wide }]
  ]) {
    if (only && !name.includes(only)) continue;
    await withPage(options, async page => {
      await hoverShots(page, name);
      await focusShots(page, name);
      await selectionShots(page, name);
    });
  }

  // 资源占用条目编辑栏：进入编辑时的样子；有“选中”按钮的版本再截一张选中物理内存（下面只摆它的来源）。
  for (const [name, options] of [
    ["editor-standard-light-wide-zh-CN", { uiStyle: "standard", theme: "light", language: "zh-CN", viewport: viewports.wide }],
    ["editor-standard-dark-narrow-de-DE", { uiStyle: "standard", theme: "dark", language: "de-DE", viewport: viewports.narrow }]
  ]) {
    if (only && !name.includes(only)) continue;
    await withPage(options, async page => {
      await page.locator('[data-focus-key="resource-breakdown-edit"]').click();
      const editor = page.locator(".resource-bar-editor");
      await editor.waitFor();
      await page.waitForTimeout(300);
      await shot(page, `${name}-entry`);
      const memory = editor.locator(".resource-bar-select").filter({ hasText: /^\s*(物理内存实际占用|Physisch belegter Arbeitsspeicher)\s*$/ });
      if (await memory.count() === 1) {
        await memory.click();
        await page.waitForTimeout(300);
        await shot(page, `${name}-memory-selected`);
      }
    });
  }

  // 悬停提示：CPU 条放到视口顶部（提示只能往下开，正下方是物理内存那一行），悬停第一段，截整个视口。
  for (const [name, options] of [
    ["tooltip-standard-light-1100-zh-CN", { uiStyle: "standard", theme: "light", language: "zh-CN" }],
    ["tooltip-standard-dark-1100-zh-CN", { uiStyle: "standard", theme: "dark", language: "zh-CN" }],
    ["tooltip-standard-light-1100-de-DE", { uiStyle: "standard", theme: "light", language: "de-DE" }],
    ["tooltip-standard-dark-1100-de-DE", { uiStyle: "standard", theme: "dark", language: "de-DE" }],
    ["tooltip-soft-dark-1100-de-DE", { uiStyle: "soft", theme: "dark", language: "de-DE" }],
    ["tooltip-outline-graphite-1100-de-DE", { uiStyle: "outline", theme: "graphite", language: "de-DE" }],
    ["tooltip-terminal-phosphor-1100-zh-CN", { uiStyle: "terminal", theme: "phosphor", language: "zh-CN" }],
    ["tooltip-terminal-amber-1100-de-DE", { uiStyle: "terminal", theme: "amber", language: "de-DE" }]
  ]) {
    if (only && !name.includes(only)) continue;
    await withPage({ ...options, viewport: { width: 1100, height: 800 }, animations: "none" }, async page => {
      await page.evaluate(() => document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]').scrollIntoView({ block: "start" }));
      await page.waitForTimeout(300);
      const box = await page.locator('.resource-bar-track[data-metric-id="cpu.usage"]').boundingBox();
      await page.mouse.move(box.x + 30, box.y + box.height / 2);
      await page.waitForTimeout(500);
      const path = join(outDir, `${name}.png`);
      await page.screenshot({ path });
      manifest.push({ name, path });
    });
  }

  if (!only || "motion".includes(only) || only === "motion") {
    for (const animations of ["desktop", "none"]) {
      await withPage({ uiStyle: "standard", theme: "light", language: "zh-CN", viewport: viewports.wide, animations }, async page => {
        for (const [step, generation] of [["values", 2], ["leave", 3], ["return", 4]]) {
          const frames = await recordTransition(page, generation);
          writeFileSync(join(outDir, `motion-${animations}-${step}.json`), JSON.stringify(frames, null, 1));
          manifest.push({ name: `motion-${animations}-${step}`, frames: frames.samples.length, summary: frames.summary });
        }
      });
    }
  }
  writeFileSync(join(outDir, "manifest.json"), JSON.stringify(manifest, null, 2));
  process.stdout.write(`AUDIT SHOTS OK ${manifest.length} -> ${outDir}\n`);
} finally {
  await browser?.close();
  if (server.exitCode === null) server.kill();
}

async function withPage(options, run) {
  const context = await browser.newContext({
    locale: options.language,
    viewport: options.viewport,
    deviceScaleFactor: 1,
    ...(options.forcedColors ? { forcedColors: options.forcedColors } : {}),
    ...(options.colorScheme ? { colorScheme: options.colorScheme } : {})
  });
  const preferences = pointPreferences();
  preferences.appearance.uiStyle = options.uiStyle;
  preferences.appearance.theme = options.theme;
  preferences.appearance.language = options.language;
  if (options.barColorMode) preferences.appearance.barColorMode = options.barColorMode;
  if (options.animations) { preferences.appearance.animationStyle = options.animations; preferences.appearance.autoDisableAnimations = false; }
  await context.addInitScript(([key, text]) => {
    if (location.origin !== "null" && localStorage.getItem(key) === null) localStorage.setItem(key, text);
  }, [frontendPreferencesKey, JSON.stringify(preferences)]);
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  try {
    await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
    await setScenario({ resourceSoftwareMix: "audit", resourceBreakdownGeneration: 0 });
    await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
    await waitForBars(page);
    await run(page);
    if (errors.length) manifest.push({ name: "page-errors", errors });
  } finally {
    await context.close();
  }
}

async function waitForBars(page) {
  await page.waitForFunction(() => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return canvas instanceof HTMLCanvasElement && canvas.width > 100 && Number(canvas.dataset.resourceSegmentCount) >= 10;
  }, null, { timeout: 15000 });
  await page.evaluate(() => document.fonts?.ready);
  await page.waitForTimeout(700);
}

async function shot(page, name) {
  const panel = page.locator(".resource-breakdown-panel").first();
  await panel.scrollIntoViewIfNeeded();
  const path = join(outDir, `${name}.png`);
  await panel.screenshot({ path });
  manifest.push({ name, path });
}

async function advanceGeneration(page, generation) {
  const before = await canvasSignature(page);
  await setScenario({ resourceBreakdownGeneration: generation });
  await page.waitForFunction(previous => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return canvas instanceof HTMLCanvasElement && canvas.toDataURL().length !== previous;
  }, before, { timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(900);
}

function canvasSignature(page) {
  return page.evaluate(() => document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas')?.toDataURL().length ?? 0);
}

async function hoverShots(page, name) {
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await track.scrollIntoViewIfNeeded();
  const box = await track.boundingBox();
  const item = page.locator('.resource-breakdown-item:has(.resource-bar-track[data-metric-id="memory.usage"])');
  // 逐像素扫过整条：记下指针能指到哪些段、各在哪些横坐标，和条上的段数（aria-setsize）比。
  const reach = new Map();
  let setSize = 0;
  for (let x = 1; x < box.width - 1; x++) {
    await page.mouse.move(box.x + x, box.y + box.height / 2);
    const state = await page.evaluate(() => {
      const proxy = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] .resource-segment-interaction-proxy');
      return proxy ? [proxy.getAttribute("data-software-id"), Number(proxy.getAttribute("aria-setsize"))] : [null, 0];
    });
    if (state[0]) {
      if (!reach.has(state[0])) reach.set(state[0], []);
      reach.get(state[0]).push(x);
      setSize = state[1];
    }
  }
  const reachable = [...reach.entries()].map(([id, xs]) => ({ id, from: xs[0], to: xs.at(-1), px: xs.length }));
  manifest.push({ name: `${name}-pointer-reach`, setSize, reached: reachable.length, reachable });
  const picks = [
    ["first", "frontend-harness.audit.browser"],
    ["tiny", "frontend-harness.audit.tiniest"],
    ["residual", "resource-residual:memory.usage"],
    ["empty", "__resource-empty:memory.usage"]
  ];
  for (const [label, id] of picks) {
    const xs = reach.get(id);
    if (!xs) {
      manifest.push({ name: `${name}-hover-${label}`, unreachable: id });
      continue;
    }
    await page.mouse.move(box.x + xs[Math.floor(xs.length / 2)], box.y + box.height / 2);
    await page.waitForTimeout(260);
    const itemBox = await item.boundingBox();
    const clip = { x: 0, y: Math.max(0, itemBox.y - 150), width: page.viewportSize().width, height: itemBox.height + 190 };
    const path = join(outDir, `${name}-hover-${label}.png`);
    await page.screenshot({ path, clip });
    manifest.push({ name: `${name}-hover-${label}`, path });
  }
  await page.mouse.move(2, 2);
}

async function focusShots(page, name) {
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await page.mouse.move(2, 2);
  await track.focus();
  await page.keyboard.press("ArrowRight");
  await page.keyboard.press("ArrowRight");
  await page.waitForTimeout(300);
  const item = page.locator('.resource-breakdown-item:has(.resource-bar-track[data-metric-id="memory.usage"])');
  const path = join(outDir, `${name}-focus.png`);
  await item.screenshot({ path });
  const state = await page.evaluate(() => ({
    active: document.activeElement?.getAttribute("data-metric-id"),
    descendant: document.activeElement?.getAttribute("aria-activedescendant"),
    option: document.querySelector(".resource-segment-interaction-proxy")?.getAttribute("aria-label")
  }));
  manifest.push({ name: `${name}-focus`, path, state });
  await page.keyboard.press("Escape");
}

async function selectionShots(page, name) {
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await track.focus();
  await page.keyboard.press("End");
  await page.waitForTimeout(500);
  const item = page.locator('.resource-breakdown-item:has(.resource-bar-track[data-metric-id="memory.usage"])');
  const path = join(outDir, `${name}-selected-end.png`);
  await item.screenshot({ path });
  manifest.push({ name: `${name}-selected-end`, path });
}

async function recordTransition(page, generation) {
  await page.evaluate(() => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    const context = canvas.getContext("2d");
    const ratio = canvas.width / canvas.getBoundingClientRect().width;
    const overscan = Number.parseFloat(getComputedStyle(canvas.parentElement).getPropertyValue("--segment-paint-overscan")) * ratio;
    const samples = [];
    window.__barFrames = samples;
    const started = performance.now();
    const sample = now => {
      if (now - started > 4000) return;
      const y = Math.floor(canvas.height - overscan - 3 * ratio);
      const data = context.getImageData(0, y, canvas.width, 1).data;
      const edges = [];
      let lastPainted = -1;
      for (let x = 0; x < canvas.width; x++) {
        if (data[x * 4 + 3] > 0) lastPainted = x;
        if (x > 0 && (data[x * 4] !== data[x * 4 - 4] || data[x * 4 + 1] !== data[x * 4 - 3] || data[x * 4 + 2] !== data[x * 4 - 2])) edges.push(x);
      }
      // 条上露底：最后一个有颜色的像素左边还有透明像素。
      let holes = 0;
      for (let x = 0; x < lastPainted; x++) if (data[x * 4 + 3] === 0) holes++;
      samples.push({ t: Math.round(now - started), edges, holes, width: canvas.width });
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  });
  await setScenario({ resourceBreakdownGeneration: generation });
  await page.waitForTimeout(4200);
  const samples = await page.evaluate(() => window.__barFrames);
  // 每帧最右边界（已用部分的末端）：看过冲、回退、停顿。
  // 前几条分界（最左边几段的边）不受斜纹影响，用它们看过渡是否连续。
  const ends = samples.map(sample => ({ t: sample.t, end: sample.edges.at(-1) ?? 0, count: sample.edges.length, holes: sample.holes,
    first: sample.edges.slice(0, 4), width: sample.width }));
  const changes = ends.filter((entry, index) => index > 0 && entry.first.join() !== ends[index - 1].first.join());
  const first = changes[0]?.t ?? null;
  const last = changes.at(-1)?.t ?? null;
  // 第二条分界（第一段的右边）的走向：有没有来回（过冲、抖动）。
  const track = changes.map(entry => entry.first[1] ?? 0);
  const direction = track.length ? Math.sign(track.at(-1) - (ends[0].first[1] ?? 0)) : 0;
  const reversals = track.filter((value, index) => index > 0 && direction !== 0 && Math.sign(value - track[index - 1]) === -direction).length;
  return { summary: { firstChangeMs: first, lastChangeMs: last, durationMs: first === null ? null : last - first, changedFrames: changes.length, reversals,
    edgeCounts: [...new Set(ends.map(e => e.count))], framesWithHoles: ends.filter(e => e.holes > 0).length, maxHoles: Math.max(0, ...ends.map(e => e.holes)) }, samples: ends };
}

async function setScenario(patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(patch) });
  if (!response.ok) throw new Error(`scenario ${response.status}`);
}

function playwrightModule() {
  const candidate = [process.env.RM_PLAYWRIGHT_MODULE, resolve(projectRoot, "node_modules/playwright/index.mjs")].filter(Boolean).find(existsSync);
  if (!candidate) throw new Error("Playwright is unavailable");
  return candidate;
}

function browserExecutable() {
  return process.env.RM_PLAYWRIGHT_BROWSER && existsSync(process.env.RM_PLAYWRIGHT_BROWSER) ? process.env.RM_PLAYWRIGHT_BROWSER : null;
}

async function waitForServer() {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error(`Harness exited early.\n${serverOutput}`);
    try {
      if ((await fetch(`${baseUrl}/__test/scenario`)).ok) return;
    } catch {
      // not bound yet
    }
    await new Promise(done => setTimeout(done, 100));
  }
  throw new Error(`Harness did not become ready.\n${serverOutput}`);
}

async function reservePort() {
  const probe = createServer();
  await new Promise((done, fail) => { probe.once("error", fail); probe.listen(0, "127.0.0.1", done); });
  const address = probe.address();
  await new Promise(done => probe.close(done));
  return typeof address === "object" && address ? address.port : 0;
}

function parseArgs(list) {
  const result = {};
  let index = 0;
  while (index < list.length) {
    const key = list[index].replace(/^--/, "");
    result[key] = list[index + 1];
    index += 2;
  }
  return result;
}

function required(values, key) {
  if (!values[key]) throw new Error(`--${key} is required`);
  return values[key];
}
