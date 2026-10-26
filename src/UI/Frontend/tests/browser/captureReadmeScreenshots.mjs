// README 截图：从模拟后端的虚构整机（场景 readmeShowcase，数据在 readmeShowcaseFixture.js）重拍 docs/screenshots/<en|zh>/ 下的全部截图。
// 用法：node --experimental-transform-types tests/browser/captureReadmeScreenshots.mjs [--out <目录>] [--failures <目录>] [--languages en-US,zh-CN] [--pages 01-dashboard,...]
// 先 vite build（模拟服务器发布 src/Core/wwwroot）；用 RM_PLAYWRIGHT_MODULE / RM_PLAYWRIGHT_BROWSER 指的 Playwright 和浏览器。
// 窗口：1281×801 CSS 像素、缩放 1.25，输出 1601×1001 的图，与原截图相同（原图是 125% 缩放下的应用窗口）；风扇曲线只截曲线那一块。
// 每页都等到真实内容出现（不是空状态、加载中或占位）再截；任何一页等不到就报错退出，不写半成品。
import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { existsSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { frontendPreferencesKey } from "./frontendPreferencesFixture.js";
import { waitForContentSettled } from "./contentSettled.js";
import { showcaseFieldDevices } from "./readmeShowcaseFixture.js";

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const repositoryRoot = resolve(projectRoot, "../../..");
const option = (name, fallback) => {
  const index = process.argv.indexOf(name);
  return index > 0 ? process.argv[index + 1] : fallback;
};
const outRoot = resolve(option("--out", resolve(repositoryRoot, "docs/screenshots")));
const failureRoot = resolve(option("--failures", resolve(tmpdir(), "rm-readme-screenshot-failures")));
const languages = option("--languages", "en-US,zh-CN").split(",");
const folderByLanguage = { "en-US": "en", "zh-CN": "zh" };
for (const language of languages) if (!folderByLanguage[language]) throw new Error(`no screenshot folder for ${language}`);

const port = await reservePort();
const baseUrl = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, [...process.execArgv, resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
  cwd: projectRoot, env: { ...process.env, RM_FRONTEND_HARNESS_PORT: String(port) }, stdio: ["ignore", "pipe", "pipe"]
});
let serverOutput = "";
server.stdout.on("data", (c) => { serverOutput += c; });
server.stderr.on("data", (c) => { serverOutput += c; });

const modulePath = [process.env.RM_PLAYWRIGHT_MODULE, resolve(projectRoot, "node_modules/playwright/index.mjs")].filter(Boolean).find((p) => existsSync(p));
const { chromium } = await import(pathToFileURL(modulePath).href);
const executablePath = [process.env.RM_PLAYWRIGHT_BROWSER, "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"].filter(Boolean).find((p) => existsSync(p));

/** 一张截图：文件名、怎么到那一页并等到内容、截哪里（默认整个窗口）。 */
const shots = [
  { file: "01-dashboard", open: openDashboard },
  { file: "02-resource-list", open: openResourceList },
  { file: "03-software", open: openSoftware },
  { file: "04-optimization", open: openOptimization },
  { file: "05-device-topology", open: openDeviceTopology },
  { file: "06-cpu-topology", open: openCpuTopology },
  { file: "07-gpu-scheduling", open: openGpuScheduling },
  { file: "08-settings", open: openSettings },
  { file: "09-app-policy", open: openAppPolicy },
  { file: "11-hardware-control", open: openHardwareControl },
  { file: "12-fan-curve", open: openFanCurve },
  { file: "13-disk-usage", open: openDiskUsage }
];
const selected = option("--pages", null)?.split(",");
const plan = selected ? shots.filter((shot) => selected.includes(shot.file)) : shots;
if (selected && plan.length !== selected.length) throw new Error(`unknown page in --pages: ${selected.join(",")}`);

// 后端场景：虚构整机，加上与真机安装相同的能力（性能优化、启动时选 GPU、运行时效果）。
const showcaseScenario = {
  readmeShowcase: true, optimizationRuntime: true, gpuPlacementEnabled: true, runtimeEffectOwners: true,
  metricFieldDevices: showcaseFieldDevices
};

let browser;
const written = [];
try {
  await waitForServer();
  browser = await chromium.launch({ headless: true, executablePath });
  for (const language of languages) {
    const app = { ...await loadAppCopy(language), settingsText: await loadSettingsText(language) };
    for (const shot of plan) {
      await postScenario("/__test/scenario/reset", {});
      await postScenario("/__test/scenario", { ...showcaseScenario });
      const context = await browser.newContext({ locale: language, viewport: { width: 1281, height: 801 }, deviceScaleFactor: 1.25 });
      const preferences = pointPreferences();
      preferences.appearance.language = language;
      // 标题栏开机时长按本机状态的刷新间隔读，默认间隔太长；改成 1 秒，打开页面后很快就有值。
      preferences.refresh.localSystem = { mode: "custom", preset: "responsive", customValue: 1_000 };
      await context.addInitScript(([key, text]) => {
        if (location.origin === "null") return;
        localStorage.setItem(key, text);
      }, [frontendPreferencesKey, JSON.stringify(preferences)]);
      const page = await context.newPage();
      const errors = [];
      page.on("pageerror", (error) => errors.push(String(error)));
      try {
        await page.goto(baseUrl);
        await page.locator(".shell-pages").waitFor();
        const target = (await shot.open(page, app)) ?? page;
        await waitForContentSettled(page, 5000);
        await page.waitForTimeout(400);
        if (errors.length) throw new Error(errors.join("\n"));
        const path = resolve(outRoot, folderByLanguage[language], `${shot.file}.png`);
        mkdirSync(resolve(path, ".."), { recursive: true });
        await target.screenshot({ path, animations: "disabled", caret: "hide" });
        written.push(path);
        process.stdout.write(`${language} ${shot.file} -> ${path}\n`);
      } catch (error) {
        // 等不到内容时留一张失败现场，便于看缺了什么；写到 --failures 目录（默认系统临时目录），不写进截图目录。
        const failure = resolve(failureRoot, `failure-${folderByLanguage[language]}-${shot.file}.png`);
        mkdirSync(failureRoot, { recursive: true });
        await page.screenshot({ path: failure }).catch(() => undefined);
        throw new Error(`${language} ${shot.file}: ${error.message}\nfailure screenshot: ${failure}`, { cause: error });
      } finally {
        await context.close();
      }
    }
  }
  process.stdout.write(`${written.length} screenshots written\n`);
} finally {
  await browser?.close();
  server.kill();
}

async function primary(page, app, pageId) {
  await page.locator(".shell-pages").getByRole("button", { name: app.page[pageId], exact: true }).click();
  await waitForContentSettled(page, 5000);
}

// 页面上看得见这些字（目录图的标签、卷标等由数据决定的文字）。
async function waitForText(page, texts) {
  await page.waitForFunction((expected) => expected.every((text) => document.body.textContent.includes(text)), texts, { timeout: 15_000 });
}

// 标题栏的开机时长要有值（不是“--”）。
async function waitForUptime(page) {
  await page.waitForFunction(() => /\d:\d\d/u.test(document.querySelector(".shell-uptime")?.textContent ?? ""), undefined, { timeout: 15_000 });
}

async function openDashboard(page, app) {
  await primary(page, app, "monitor");
  // 卡片和资源条都要有值：卡片上不能留“--”，资源条要有软件分段。
  await page.locator(".metric-card").nth(7).waitFor({ timeout: 15_000 });
  await page.waitForFunction(() => [...document.querySelectorAll(".metric-card .metric-value")].every((node) => !/^\s*(--|-)\s*$/u.test(node.textContent ?? "")),
    undefined, { timeout: 15_000 });
  await waitForUptime(page);
}

async function openResourceList(page, app) {
  await primary(page, app, "monitor");
  const panel = page.locator(".resource-table-panel");
  // 资源表滚到窗口顶部（标题栏下面）；看得见以后才订阅和渲染行，再等软件行到齐。
  await panel.waitFor({ timeout: 15_000 });
  await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
  await panel.locator(".resource-table-row").nth(12).waitFor({ timeout: 15_000 });
  await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
  await waitForUptime(page);
}

async function openSoftwareCategory(page, app, kind) {
  await primary(page, app, "components");
  await page.getByRole("tablist", { name: app.management.categoryNav, exact: true })
    .getByRole("tab", { name: new RegExp(`^${app.softwareKind[kind]}\\s*\\d+$`, "u") }).click();
  await waitForContentSettled(page, 5000);
}

async function openSoftware(page, app) {
  await openSoftwareCategory(page, app, "other");
  await page.locator(".software-item").nth(9).waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

async function openAppPolicy(page, app) {
  await openSoftwareCategory(page, app, "game");
  const item = page.locator(".software-item").filter({ hasText: "Kestrel Rally" });
  await item.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Kestrel Rally" });
  await dialog.getByRole("tab", { name: app.softwareDetail.tab.policy, exact: true }).click();
  await dialog.getByRole("combobox").first().waitFor({ timeout: 15_000 });
  await page.waitForFunction(() => !document.querySelector(".software-protection-section input")?.disabled, undefined, { timeout: 15_000 });
  await waitForUptime(page);
}

async function openOptimization(page, app) {
  await primary(page, app, "optimization");
  await page.locator(".optimization-report").nth(2).waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

// 设置 → 外观，从页首开始（左侧分区导航也在画面里）。
async function openSettings(page, app) {
  await primary(page, app, "settings");
  await page.locator(".settings-nav").getByRole("button", { name: app.settingsText.sections.appearance, exact: true }).click();
  await waitForContentSettled(page, 5000);
  await page.getByRole("button", { name: app.settingsText.appearance.languageSelectLabel, exact: true }).waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

// 控制面：显卡、处理器和风扇卡片都在，读回值（例如温度墙）已经到了。
async function openHardwareControl(page, app) {
  await primary(page, app, "control");
  await page.locator(".control-object").nth(3).waitFor({ timeout: 15_000 });
  await page.locator(".control-object").filter({ hasText: "84 °C" }).first().waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

// 风扇卡片：选「固件写入」，勾上转速曲线，只截这一项（勾选框、执行方式和曲线图）。
async function openFanCurve(page, app) {
  await openHardwareControl(page, app);
  const fan = page.locator(".control-object").filter({ has: page.locator(".control-fan-ownership") });
  await fan.getByRole("radio", { name: app.control.term["firmware-write"], exact: true }).click();
  const curveLabel = app.control.presentation.labels["fan.curve"];
  await fan.getByRole("checkbox", { name: curveLabel, exact: true }).check();
  const chart = fan.locator(".fan-curve-chart");
  await chart.waitFor({ timeout: 15_000 });
  const item = fan.locator(".control-capability").filter({ has: page.locator(".fan-curve-chart") });
  await item.scrollIntoViewIfNeeded();
  return item;
}

// 磁盘占用：三个卷，以及上一次扫描读出的 C: 目录图（标签都画在 canvas 上）。
async function openDiskUsage(page, app) {
  await primary(page, app, "diskUsage");
  await waitForText(page, ["Portable"]);
  // 目录图画在 canvas 上：等到中间已经画上颜色。
  await page.waitForFunction(() => {
    const canvas = document.querySelector(".disk-usage-treemap-canvas");
    const context = canvas?.getContext("2d");
    if (!context || canvas.width === 0 || canvas.height === 0) return false;
    return context.getImageData(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1).data[3] > 0;
  }, undefined, { timeout: 15_000 });
  await waitForUptime(page);
}

async function openDetailsTab(page, app, tab) {
  await primary(page, app, "details");
  await page.locator(".page-subnav").getByRole("tab", { name: app.misc.detailsTab[tab], exact: true }).click();
  await waitForContentSettled(page, 5000);
}

async function openDeviceTopology(page, app) {
  await openDetailsTab(page, app, "device");
  await page.locator(".device-topology-panel .device-port-item").nth(5).waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

async function openCpuTopology(page, app) {
  await openDetailsTab(page, app, "cpu");
  const model = page.getByRole("region", { name: app.cpuTopology.modelLabel, exact: true });
  await model.locator(".cpu-topology-meta strong").waitFor({ timeout: 15_000 });
  await waitForUptime(page);
}

// GPU 调度模型：两块显卡都有性能分、频率和占用。
async function openGpuScheduling(page, app) {
  await openDetailsTab(page, app, "gpu");
  await waitForText(page, ["Demo Graphics X60", "Demo Integrated Graphics", "4870"]);
  await waitForUptime(page);
}

async function postScenario(path, body) {
  const response = await fetch(`${baseUrl}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path} failed: ${response.status} ${await response.text()}`);
}

// 系统分配的空闲端口；避开已安装的 Resource Manager 服务和界面用的 9321、9333。
async function reservePort() {
  for (let attempt = 0; attempt < 10; attempt += 1) {
    const s = createServer();
    await new Promise((ok, fail) => { s.once("error", fail); s.listen(0, "127.0.0.1", ok); });
    const p = s.address().port;
    await new Promise((ok) => s.close(ok));
    if (p !== 9321 && p !== 9333) return p;
  }
  throw new Error("no free port outside 9321/9333");
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
