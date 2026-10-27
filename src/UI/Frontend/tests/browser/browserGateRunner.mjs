import { fork, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { takeAssertionCount } from "./assertionCounter.mjs";
import { localizationLanguageOrder } from "./localizationJourney.js";
import usabilityProbe from "./usabilityProbe.js";

/*
 * 浏览器门的运行机制。旅程登记仍在 runBrowserGate.mjs 的 allJourneys 里；那个文件把登记表交给这里。
 *
 * 任务：每个选中的旅程是一个任务。按语言拆分的旅程（LANGUAGE_SHARDED_JOURNEYS）在多于一个工作进程时
 * 每种语言一个任务，旅程收到 { languages: [语言] }；只有一个工作进程时整条旅程一个任务，与原来相同。
 *
 * 工作进程：同时开 RM_BROWSER_WORKERS 个（或 --workers N；默认 DEFAULT_BROWSER_WORKERS），每个是
 * `runBrowserGate.mjs --worker`，有自己的模拟服务器和一个浏览器，一次跑一个任务：每个任务前重置模拟
 * 服务器的场景，开新的浏览器上下文和页面，结束后关掉上下文。隔离与原来串行跑时相同（原来也是一个浏览器、
 * 每个旅程重置场景和新上下文），只是旅程分到几个这样的进程里。任务按 START_FIRST 先排最慢的，其余按登记
 * 顺序；空出来的工作进程领下一个任务。
 *
 * 输出：工作进程把任务的全部输出缓存起来随结果交回；每个任务结束时打印一行
 * `PASS|FAIL 名称 用时 assertions=N worker=K`，后面跟它的输出，不会交错。assertions 是该任务执行的
 * node:assert 方法调用数（见 assertionCounter.mjs）。第一个失败之后不再派新任务，在跑的任务跑完；最后
 * 打印失败任务的错误和诊断，以 1 退出。全部通过时打印与原来相同形状的 JSON 汇总，再打印用时汇总。
 * 定向续跑可显式传 --keep-going，失败仍记录并以 1 退出，但其余任务继续派发；默认值为 DEFAULT_KEEP_GOING。
 *
 * 环境变量：RM_BROWSER_JOURNEY（逗号分隔的旅程名，不设就全部）、RM_LOCALIZATION_LANGUAGES（逗号分隔的
 * 语言 id，只检查这几种语言）、RM_BROWSER_WORKERS。其余（RM_PLAYWRIGHT_MODULE 等）原样传给工作进程。
 */

// 3 个工作进程覆盖全部旅程后的进程树实测峰值 2.66 GiB，距 6 GiB Job 上限留出 3.34 GiB 余量。
// 4 进程测量两次被全机内存压力暂停，未形成完整测量，不据此扩大默认值。
export const DEFAULT_BROWSER_WORKERS = 3;
const DEFAULT_KEEP_GOING = false;

// 先开始的旅程，按用时从长到短（2026-10-26 串行基线）。没列出的旅程按登记顺序排在后面。
const START_FIRST = [
  "management-feedback",
  "device-topology-source",
  "localization",
  "overlay-source-choice",
  "page-tabs-consistency",
  "disk-usage-feedback-refresh",
  "theme-consistency",
  "software-unclassified",
  "resource-bar-proportion",
  "dashboard-preferences"
];

// 按语言拆分的旅程：名称 → 返回完整语言顺序的函数。
const LANGUAGE_SHARDED_JOURNEYS = new Map([["localization", localizationLanguageOrder]]);

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));

export async function runBrowserGate(allJourneys, entryUrl) {
  if (process.argv.includes("--worker")) await runWorker(allJourneys);
  else await runCoordinator(allJourneys, entryUrl);
}

async function runCoordinator(allJourneys, entryUrl) {
  const journeyFilter = process.env.RM_BROWSER_JOURNEY?.trim();
  const requestedJourneys = new Set(journeyFilter?.split(",").map(name => name.trim()).filter(Boolean));
  const journeys = journeyFilter
    ? allJourneys.filter(([name]) => requestedJourneys.has(name))
    : allJourneys;
  if (journeys.length === 0 || (journeyFilter && journeys.length !== requestedJourneys.size)) {
    throw new Error(`Unknown browser journey: ${journeyFilter}`);
  }
  const workerCount = readWorkerCount();
  const languages = process.env.RM_LOCALIZATION_LANGUAGES?.split(",").map(id => id.trim()).filter(Boolean);
  const jobs = orderJobs(buildJobs(journeys, workerCount, languages?.length ? languages : undefined));
  const started = Date.now();
  // 定向收集剩余覆盖时可显式 --keep-going；默认仍在首个失败后停止派发。
  const keepGoing = process.argv.includes("--keep-going") || DEFAULT_KEEP_GOING;
  const outcome = await runPool(jobs, Math.min(workerCount, jobs.length), fileURLToPath(entryUrl), keepGoing);
  report(jobs, outcome, workerCount, Date.now() - started);
}

function readWorkerCount() {
  const flag = process.argv.indexOf("--workers");
  const raw = flag >= 0 ? process.argv[flag + 1] : (process.env.RM_BROWSER_WORKERS || String(DEFAULT_BROWSER_WORKERS));
  const count = Number(raw);
  if (!Number.isInteger(count) || count < 1) throw new Error(`The browser worker count must be a positive integer: ${raw}`);
  return count;
}

function buildJobs(journeys, workerCount, languages) {
  const jobs = [];
  journeys.forEach(([name], registrationIndex) => {
    const languageOrder = LANGUAGE_SHARDED_JOURNEYS.get(name);
    if (languageOrder && workerCount > 1) {
      const selected = languageOrder().filter(id => !languages || languages.includes(id));
      const unknown = (languages ?? []).filter(id => !languageOrder().includes(id));
      if (unknown.length > 0 || selected.length === 0) throw new Error(`RM_LOCALIZATION_LANGUAGES has no or unknown languages: ${process.env.RM_LOCALIZATION_LANGUAGES}`);
      for (const language of selected) {
        jobs.push({ name, label: `${name}[${language}]`, shard: language, options: { languages: [language] }, registrationIndex });
      }
    } else {
      jobs.push({ name, label: name, shard: null, options: languageOrder && languages ? { languages } : {}, registrationIndex });
    }
  });
  return jobs.map((job, id) => ({ ...job, id }));
}

function orderJobs(jobs) {
  const rank = (job) => {
    const position = START_FIRST.indexOf(job.name);
    return position < 0 ? START_FIRST.length : position;
  };
  return [...jobs].sort((left, right) => rank(left) - rank(right) || left.id - right.id);
}

async function runPool(jobs, workerCount, entryPath, keepGoing) {
  const pending = [...jobs];
  const results = new Map();
  let stopping = false;
  let startupError = null;
  const workers = [];

  await new Promise((resolveAll) => {
    let live = 0;
    const finishIfDone = () => {
      if (live === 0) resolveAll();
    };
    const sendTo = (worker, message) => {
      if (worker.child.connected) worker.child.send(message);
    };
    const dispatch = (worker) => {
      if (stopping || pending.length === 0) {
        worker.current = null;
        sendTo(worker, { type: "stop" });
        return;
      }
      worker.current = pending.shift();
      sendTo(worker, { type: "run", job: worker.current });
    };
    for (let index = 0; index < workerCount; index += 1) {
      const child = fork(entryPath, ["--worker"], {
        cwd: projectRoot,
        execArgv: process.execArgv,
        stdio: ["ignore", "pipe", "pipe", "ipc"]
      });
      const worker = { index: index + 1, child, current: null, rawOutput: "" };
      workers.push(worker);
      live += 1;
      // 工作进程的输出都经消息交回；直接写到管道上的只有崩溃信息，崩溃时附在当前任务上。
      child.stdout.on("data", (chunk) => { worker.rawOutput += chunk; });
      child.stderr.on("data", (chunk) => { worker.rawOutput += chunk; });
      child.on("message", (message) => {
        if (message.type === "ready") {
          dispatch(worker);
        } else if (message.type === "startup-failed") {
          startupError ??= `Browser worker ${worker.index} could not start:\n${message.error}\n${message.output}`;
          stopping = true;
          sendTo(worker, { type: "stop" });
        } else if (message.type === "result") {
          const job = worker.current;
          results.set(job.id, { ...message, worker: worker.index });
          printJob(job, results.get(job.id));
          if (!message.ok && !keepGoing) stopping = true;
          dispatch(worker);
        }
      });
      child.on("exit", (code, signal) => {
        if (worker.current && !results.has(worker.current.id)) {
          const result = {
            ok: false, ms: 0, assertions: 0, worker: worker.index, output: "",
            error: `Browser worker ${worker.index} exited (${signal ?? code}) while running ${worker.current.label}.\n${worker.rawOutput}`
          };
          results.set(worker.current.id, result);
          printJob(worker.current, result);
          stopping = true;
        } else if (code !== 0 && !startupError && !stopping) {
          startupError = `Browser worker ${worker.index} exited (${signal ?? code}).\n${worker.rawOutput}`;
          stopping = true;
        }
        worker.current = null;
        live -= 1;
        finishIfDone();
      });
    }
  });
  return { results, startupError };
}

function printJob(job, result) {
  const status = result.ok ? "PASS" : "FAIL";
  let block = `${status} ${job.label} ${(result.ms / 1000).toFixed(1)}s assertions=${result.assertions} worker=${result.worker}\n`;
  if (result.output) block += result.output.endsWith("\n") ? result.output : `${result.output}\n`;
  if (!result.ok) block += `${result.error}\n`;
  process.stdout.write(block);
}

function report(jobs, { results, startupError }, workerCount, wallMs) {
  const byRegistration = [...jobs].sort((left, right) => left.id - right.id);
  const failed = byRegistration.filter((job) => results.get(job.id) && !results.get(job.id).ok);
  const notRun = byRegistration.filter((job) => !results.has(job.id));
  const passed = byRegistration.filter((job) => results.get(job.id)?.ok);
  if (failed.length === 0 && notRun.length === 0 && !startupError) {
    process.stdout.write(`${JSON.stringify({
      journeys: passed.map((job) => ({ name: job.name, shard: job.shard, ...results.get(job.id).entry }))
    }, null, 2)}\n`);
  }
  const timed = [...results.entries()].map(([id, result]) => ({ job: jobs.find(job => job.id === id), result }));
  const slowest = [...timed].sort((left, right) => right.result.ms - left.result.ms).slice(0, 10);
  const sumMs = timed.reduce((sum, { result }) => sum + result.ms, 0);
  const lines = [
    `browser gate: ${jobs.length} jobs from ${new Set(jobs.map(job => job.name)).size} journeys, ${Math.min(workerCount, jobs.length)} workers, ${(wallMs / 1000).toFixed(1)}s wall, ${(sumMs / 1000).toFixed(1)}s of job time`,
    `slowest jobs: ${slowest.map(({ job, result }) => `${job.label} ${(result.ms / 1000).toFixed(1)}s`).join("; ")}`
  ];
  if (startupError) lines.push(startupError);
  for (const job of failed) lines.push(`FAILED ${job.label}`);
  if (notRun.length > 0) lines.push(`not run: ${notRun.map(job => job.label).join(", ")}`);
  process.stdout.write(`${lines.join("\n")}\n`);
  process.exitCode = failed.length > 0 || notRun.length > 0 || startupError ? 1 : 0;
}

async function runWorker(allJourneys) {
  const journeysByName = new Map(allJourneys);
  const output = captureOutput();
  let server = null;
  let browser = null;
  let serverOutput = "";
  let baseUrl = "";
  const send = (message) => new Promise((resolveSend) => process.send(message, resolveSend));
  const shutdown = async () => {
    await browser?.close().catch(() => undefined);
    if (server) await stopChildProcess(server);
  };

  try {
    server = spawn(process.execPath, ["--experimental-transform-types", resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
      cwd: projectRoot,
      env: { ...process.env, RM_FRONTEND_HARNESS_PORT: "0" },
      stdio: ["ignore", "pipe", "pipe", "ipc"]
    });
    server.stdout.on("data", (chunk) => { serverOutput += chunk; });
    server.stderr.on("data", (chunk) => { serverOutput += chunk; });
    baseUrl = await waitForServer(server, () => serverOutput);
    const { chromium } = await loadPlaywright();
    browser = await launchBrowser(chromium);
  } catch (error) {
    await send({ type: "startup-failed", error: error instanceof Error ? error.stack ?? error.message : String(error), output: output.take() });
    await shutdown();
    process.exit(1);
  }

  process.on("message", async (message) => {
    if (message.type === "stop") {
      await shutdown();
      process.exit(0);
    }
    if (message.type !== "run") return;
    const { job } = message;
    const started = Date.now();
    output.take();
    takeAssertionCount();
    const serverOutputStart = serverOutput.length;
    let outcome;
    try {
      outcome = { ok: true, entry: await runJob(journeysByName.get(job.name), job, browser, baseUrl) };
    } catch (error) {
      outcome = { ok: false, error: error instanceof Error ? error.message : String(error) };
      if (error?.diagnostic) {
        outcome.error += `\nBrowser gate diagnostic: ${JSON.stringify({ name: job.label, ...error.diagnostic, serverOutput: serverOutput.slice(serverOutputStart) })}`;
      }
    }
    await send({ type: "result", ...outcome, ms: Date.now() - started, assertions: takeAssertionCount(), output: output.take() });
  });
  await send({ type: "ready" });
}

async function runJob(journey, job, browser, baseUrl) {
  await resetFixture(baseUrl);
  const context = await browser.newContext({ locale: "zh-CN" });
  const page = await context.newPage();
  try {
    const result = await journey(page, baseUrl, job.options);
    const usability = await usabilityProbe(page);
    const defaultPerformanceState = await readDefaultPerformanceState(page);
    if (defaultPerformanceState.bootstrapPresent
      || defaultPerformanceState.apiPresent
      || defaultPerformanceState.monitorResources.length > 0) {
      throw new Error(
        `Default frontend unexpectedly enabled performance instrumentation: ${JSON.stringify(defaultPerformanceState)}`);
    }
    return {
      baseUrl,
      result,
      defaultPerformanceState,
      pageId: usability.pageId,
      viewport: usability.viewport,
      unnamedControlCount: usability.visibleUnnamedInputs.length,
      documentOverflow: usability.document.scrollWidth > usability.document.clientWidth,
      shellOverflow: Boolean(usability.shell && usability.shell.scrollWidth > usability.shell.clientWidth)
    };
  } catch (error) {
    const [requests, pageState] = await Promise.all([
      fetch(`${baseUrl}/__test/requests`)
        .then(async (response) => response.ok ? response.json() : ({ status: response.status }))
        .catch((requestError) => ({ error: String(requestError) })),
      page.evaluate(() => ({
        href: location.href,
        title: document.title,
        text: document.body?.innerText?.slice(0, 4000) ?? "",
        readyState: document.readyState
      })).catch((pageError) => ({ error: String(pageError) }))
    ]);
    const wrapped = new Error(error instanceof Error ? error.stack ?? error.message : String(error), { cause: error });
    wrapped.diagnostic = { requests, pageState };
    throw wrapped;
  } finally {
    await context.close();
  }
}

function captureOutput() {
  // 工作进程里旅程和 Playwright 的全部输出先缓存，随任务结果交回协调进程。
  let buffered = "";
  const capture = (chunk, encoding, callback) => {
    buffered += typeof chunk === "string" ? chunk : Buffer.from(chunk).toString(typeof encoding === "string" ? encoding : "utf8");
    const done = typeof encoding === "function" ? encoding : callback;
    if (typeof done === "function") done();
    return true;
  };
  process.stdout.write = capture;
  process.stderr.write = capture;
  return {
    take() {
      const taken = buffered;
      buffered = "";
      return taken;
    }
  };
}

async function readDefaultPerformanceState(page) {
  return page.evaluate(() => ({
    bootstrapPresent: Object.hasOwn(
      globalThis,
      "__resourceManagerFrontendPerformanceBootstrap"),
    apiPresent: Object.hasOwn(
      globalThis,
      "__resourceManagerFrontendPerformance"),
    monitorResources: performance.getEntriesByType("resource")
      .map((entry) => entry.name)
      .filter((name) => name.includes("FrontendPerformanceMonitor-"))
  }));
}

async function loadPlaywright() {
  const candidates = [
    process.env.RM_PLAYWRIGHT_MODULE,
    resolve(projectRoot, "node_modules/playwright/index.mjs"),
  ].filter(Boolean);
  const modulePath = candidates.find((candidate) => existsSync(candidate));
  if (!modulePath) {
    throw new Error("Playwright is unavailable. Set RM_PLAYWRIGHT_MODULE or install Playwright in the frontend project.");
  }
  return import(pathToFileURL(modulePath).href);
}

async function launchBrowser(chromium) {
  try {
    return await chromium.launch({ headless: true });
  } catch (error) {
    if (!String(error).includes("Executable doesn't exist")) {
      throw error;
    }
  }

  const systemBrowser = [
    process.env.RM_PLAYWRIGHT_BROWSER,
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"
  ].filter(Boolean).find((candidate) => existsSync(candidate));
  if (!systemBrowser) {
    throw new Error("No Playwright browser or supported system Edge/Chrome executable is available.");
  }
  return chromium.launch({ headless: true, executablePath: systemBrowser });
}

async function waitForServer(processHandle, readServerOutput) {
  // Let the harness keep the socket it binds. The worker uses only the port
  // reported by that child's ready handshake, never a released reservation.
  let listeningPort = null;
  const onMessage = (message) => {
    if (message?.type === "frontend-harness-ready" && Number.isInteger(message.port)
      && message.port > 0 && message.port <= 65535) listeningPort = message.port;
  };
  processHandle.on("message", onMessage);
  // A real slice can contain hundreds of MB. Wait for the same bound-port
  // handshake with a finite initialization allowance, not an added sleep.
  const deadline = Date.now() + (process.env.RM_REAL_SLICE ? 60_000 : 10_000);
  try {
    while (Date.now() < deadline) {
      if (processHandle.exitCode !== null) {
        throw new Error(`Frontend harness exited early (${processHandle.exitCode}).\n${readServerOutput()}`);
      }
      try {
        if (listeningPort !== null) {
          const baseUrl = `http://127.0.0.1:${listeningPort}`;
          const response = await fetch(`${baseUrl}/__test/scenario`, {
            signal: AbortSignal.timeout(Math.max(1, deadline - Date.now()))
          });
          if (response.ok) return baseUrl;
        }
      } catch {
        // Wait for the bound harness to respond before starting a journey.
      }
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
    }
    throw new Error(`Frontend harness did not become ready.\n${readServerOutput()}`);
  } finally {
    processHandle.off("message", onMessage);
  }
}

async function resetFixture(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`Unable to reset the browser fixture: ${response.status}`);
  }
}

async function stopChildProcess(processHandle) {
  if (processHandle.exitCode === null) {
    const exited = new Promise((resolveExit) => processHandle.once("exit", resolveExit));
    processHandle.kill();
    await Promise.race([exited, new Promise((resolveDelay) => setTimeout(resolveDelay, 3000))]);
    if (processHandle.exitCode === null) {
      processHandle.kill("SIGKILL");
      await Promise.race([exited, new Promise((resolveDelay) => setTimeout(resolveDelay, 1000))]);
    }
  }
  processHandle.stdout.destroy();
  processHandle.stderr.destroy();
}
