// 换页动效的取证：截图、CDP screencast 帧序列和逐帧测量，改前改后用同一份脚本，只截页面。
// 用法：node --experimental-transform-types tests/browser/capturePageMotion.mjs <输出目录> [--animations normal|none]
// 先 vite build；起 mock 服务（mockFrontendServer.mjs），用 RM_PLAYWRIGHT_MODULE / RM_PLAYWRIGHT_BROWSER 指的 Playwright 和浏览器。
// 输出：
//   shots/<名>.png          每页、每个二级分页、任务中心每个筛选分页的静止截图（视口）
//   frames/<序号>.jpg        换页序列的 screencast 帧，frames.json 记每帧的时间戳和那一刻在做哪一次切换
//   measurements.json       每次切换：点击到二级分页条出现的毫秒数、内容起始 y、逐帧的内容位置和不透明度、长任务；任务中心各分页的对话框高度
import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const outRoot = resolve(process.argv[2] ?? "");
if (!process.argv[2]) throw new Error("usage: capturePageMotion.mjs <output dir> [--animations normal|none]");
const animationsArg = process.argv.indexOf("--animations");
const animations = animationsArg > 0 ? process.argv[animationsArg + 1] : "normal";
for (const sub of ["shots", "frames"]) mkdirSync(resolve(outRoot, sub), { recursive: true });

const app = await loadAppCopy("zh-CN");
const port = await reservePort();
const baseUrl = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, [resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
  cwd: projectRoot, env: { ...process.env, RM_FRONTEND_HARNESS_PORT: String(port) }, stdio: ["ignore", "pipe", "pipe"]
});
let serverOutput = "";
server.stdout.on("data", (c) => { serverOutput += c; });
server.stderr.on("data", (c) => { serverOutput += c; });

const modulePath = [process.env.RM_PLAYWRIGHT_MODULE, resolve(projectRoot, "node_modules/playwright/index.mjs")].filter(Boolean).find((p) => existsSync(p));
const { chromium } = await import(pathToFileURL(modulePath).href);
const executablePath = [process.env.RM_PLAYWRIGHT_BROWSER, "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"].filter(Boolean).find((p) => existsSync(p));
let browser;
const result = { animations, switches: [], taskCenter: [], longTasks: [] };
try {
  await waitForServer();
  browser = await chromium.launch({ headless: true, executablePath });
  const context = await browser.newContext({ locale: "zh-CN", viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  await page.goto(baseUrl);
  await page.locator(".shell-pages").waitFor();
  await page.waitForTimeout(1500);
  if (animations !== "normal") {
    // 动画档位：直接写 body 和磁石（App 解析后写的同一处），只用于取证；旅程里走设置。
    await page.evaluate((mode) => { document.body.dataset.animations = mode; }, animations);
  }
  // 长任务：从这里起全程记。
  await page.evaluate(() => {
    window.__captureLongTasks = [];
    new PerformanceObserver((list) => { for (const e of list.getEntries()) window.__captureLongTasks.push({ start: e.startTime, duration: e.duration }); })
      .observe({ type: "longtask", buffered: false });
  });

  const cdp = await context.newCDPSession(page);
  const frames = [];
  let currentLabel = "idle";
  cdp.on("Page.screencastFrame", async (frame) => {
    const index = frames.length;
    frames.push({ index, timestamp: frame.metadata.timestamp, label: currentLabel });
    writeFileSync(resolve(outRoot, "frames", `${String(index).padStart(4, "0")}.jpg`), Buffer.from(frame.data, "base64"));
    await cdp.send("Page.screencastFrameAck", { sessionId: frame.sessionId }).catch(() => undefined);
  });

  const primary = (name) => page.locator(".shell-pages").getByRole("button", { name, exact: true });
  /** 点一下，并在页面里逐帧记 700 ms：二级分页条里有几个分页、内容起点和不透明度、主区的滚动。 */
  const measureSwitch = async (label, click) => {
    currentLabel = label;
    await page.evaluate(() => {
      const probe = { t0: null, frames: [] };
      window.__captureProbe = probe;
      window.addEventListener("click", () => { probe.t0 = performance.now(); }, { once: true, capture: true });
      const until = performance.now() + 3000;
      const tick = (now) => {
        if (probe.t0 !== null) {
          const bar = document.querySelector(".page-subnav");
          const tabs = bar ? [...bar.querySelectorAll(".page-subnav-tab")].filter((tab) => tab.getClientRects().length > 0 && !tab.closest("[inert]")) : [];
          const main = document.querySelector("main.app-shell");
          const pageElement = [...document.querySelectorAll("main .page")].find((element) => !element.closest("[inert]"));
          const rect = pageElement?.getBoundingClientRect();
          probe.frames.push({
            t: now - probe.t0,
            subnavTabs: tabs.length,
            subnavNode: bar ? (bar.__captureId ??= Math.random().toString(36).slice(2)) : null,
            pageId: pageElement?.id ?? null,
            contentTop: rect ? Math.round(rect.top * 10) / 10 : null,
            contentLeft: rect ? Math.round(rect.left * 10) / 10 : null,
            opacity: pageElement ? Number(getComputedStyle(pageElement.closest(".page-stage") ?? pageElement).opacity) : null,
            mainTop: main ? Math.round(main.getBoundingClientRect().top) : null
          });
          if (now - probe.t0 > 700) { probe.done = true; return; }
        }
        if (now > until) { probe.done = true; return; }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
    await click();
    await page.waitForFunction(() => window.__captureProbe.done === true, undefined, { timeout: 5000 });
    const probe = await page.evaluate(() => window.__captureProbe);
    const firstWithSubnav = probe.frames.find((f) => f.subnavTabs > 0);
    const settled = probe.frames.at(-1);
    result.switches.push({
      label,
      subnavAppearMs: firstWithSubnav ? Math.round(firstWithSubnav.t * 10) / 10 : null,
      settled,
      frames: probe.frames
    });
    await page.waitForTimeout(300);
  };

  await cdp.send("Page.startScreencast", { format: "jpeg", quality: 80, everyNthFrame: 1 });
  await page.waitForTimeout(300);
  const sequence = [
    ["monitor→components", app.page.components],
    ["components→details", app.page.details],
    ["details→diskUsage", app.page.diskUsage],
    ["diskUsage→details", app.page.details],
    ["details→components", app.page.components],
    ["components→details(again)", app.page.details],
    ["details→control", app.page.control],
    ["control→settings", app.page.settings],
    ["settings→monitor", app.page.monitor]
  ];
  for (const [label, name] of sequence) await measureSwitch(label, () => primary(name).click());
  await primary(app.page.details).click();
  await page.waitForTimeout(600);
  for (const [label, name] of [["details:device→gpu", app.misc.detailsTab.gpu], ["details:gpu→cpu", app.misc.detailsTab.cpu], ["details:cpu→device", app.misc.detailsTab.device]]) {
    await measureSwitch(label, () => page.locator(".page-subnav").getByRole("tab", { name, exact: true }).click());
  }
  await cdp.send("Page.stopScreencast");
  currentLabel = "idle";

  // 静止截图：每个一级页，详细信息的每个二级分页。
  const shot = async (name) => page.screenshot({ path: resolve(outRoot, "shots", `${name}.png`) });
  for (const [id, name] of [["monitor", app.page.monitor], ["components", app.page.components], ["diskUsage", app.page.diskUsage], ["control", app.page.control], ["settings", app.page.settings], ["details", app.page.details]]) {
    await primary(name).click();
    await page.waitForTimeout(900);
    await shot(`page-${id}`);
  }
  for (const [id, name] of [["gpu", app.misc.detailsTab.gpu], ["cpu", app.misc.detailsTab.cpu]]) {
    await page.locator(".page-subnav").getByRole("tab", { name, exact: true }).click();
    await page.waitForTimeout(900);
    await shot(`details-${id}`);
  }

  // 任务中心：每个筛选分页的对话框高度，切换中逐帧的高度。
  await page.locator("#taskCenterButton").click();
  const dialog = page.getByRole("dialog");
  await dialog.waitFor();
  await page.waitForTimeout(600);
  await cdp.send("Page.startScreencast", { format: "jpeg", quality: 80, everyNthFrame: 1 });
  for (const tab of [app.taskCenter.tabHistory, app.taskCenter.tabAll, app.taskCenter.tabActive, app.taskCenter.tabHistory]) {
    currentLabel = `taskCenter→${tab}`;
    const heights = await page.evaluate(() => {
      const probe = { t0: null, frames: [] };
      window.__captureDialog = probe;
      window.addEventListener("click", () => { probe.t0 = performance.now(); }, { once: true, capture: true });
      const tick = (now) => {
        const modal = document.querySelector("[role='dialog']");
        if (probe.t0 !== null && modal) {
          probe.frames.push({ t: now - probe.t0, height: Math.round(modal.getBoundingClientRect().height * 10) / 10 });
          if (now - probe.t0 > 600) { probe.done = true; return; }
        }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
      return null;
    });
    void heights;
    await dialog.getByRole("tab").filter({ hasText: tab }).click();
    await page.waitForFunction(() => window.__captureDialog.done === true, undefined, { timeout: 5000 });
    const probe = await page.evaluate(() => window.__captureDialog);
    const values = probe.frames.map((f) => f.height);
    result.taskCenter.push({ tab, min: Math.min(...values), max: Math.max(...values), final: values.at(-1), frames: probe.frames });
    await shot(`task-center-${result.taskCenter.length}`);
  }
  await cdp.send("Page.stopScreencast");
  result.longTasks = await page.evaluate(() => window.__captureLongTasks);
  result.frames = frames;
  writeFileSync(resolve(outRoot, "frames.json"), JSON.stringify(frames, null, 1));
  writeFileSync(resolve(outRoot, "measurements.json"), JSON.stringify(result, null, 1));
  const summary = {
    animations,
    subnavAppearMs: Object.fromEntries(result.switches.map((s) => [s.label, s.subnavAppearMs])),
    contentTop: Object.fromEntries(result.switches.map((s) => [s.label, s.settled?.contentTop ?? null])),
    subnavNodeStable: result.switches.map((s) => s.settled?.subnavNode ?? null),
    taskCenterHeights: result.taskCenter.map((t) => ({ tab: t.tab, min: t.min, max: t.max, final: t.final })),
    longTasksOver50: result.longTasks.filter((t) => t.duration > 50).length,
    longestTaskMs: Math.max(0, ...result.longTasks.map((t) => t.duration)),
    frames: frames.length
  };
  writeFileSync(resolve(outRoot, "summary.json"), JSON.stringify(summary, null, 1));
  process.stdout.write(`${JSON.stringify(summary, null, 1)}\n`);
} finally {
  await browser?.close();
  server.kill();
}

async function reservePort() {
  const s = createServer();
  await new Promise((ok, fail) => { s.once("error", fail); s.listen(0, "127.0.0.1", ok); });
  const p = s.address().port;
  await new Promise((ok) => s.close(ok));
  return p;
}

async function waitForServer() {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error(`mock server exited\n${serverOutput}`);
    try { if ((await fetch(`${baseUrl}/__test/scenario`)).ok) return; } catch { /* not yet */ }
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`mock server not ready\n${serverOutput}`);
}
