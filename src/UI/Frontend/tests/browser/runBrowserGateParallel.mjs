import { spawn } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

/*
 * 并行跑几条浏览器旅程。每个任务是一个独立的 runBrowserGate 进程，各用自己的 mock 端口。
 *
 *   node tests/browser/runBrowserGateParallel.mjs [--jobs N] [--logs DIR] 任务...
 *
 * 任务写法：
 *   旅程名                          例如 metric-field-cards
 *   localization=语言,语言          九语旅程只跑这几种语言（RM_LOCALIZATION_LANGUAGES）
 *
 * --jobs：同时运行的任务数，默认 3（本机内存紧，每个任务一个 Chromium）。
 * --logs：每个任务的完整输出写到 DIR/<任务>.log，默认 .local/test-results/browser-parallel。
 * 其余环境变量（RM_PLAYWRIGHT_MODULE、RM_EDID_UI_TYPEX_STATE_JSON 等）原样传给每个任务。
 * 调用前先 npm run build；本脚本不构建。只输出每个任务一行结果，任一失败则以 1 退出。
 */

const projectRoot = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const args = process.argv.slice(2);
let concurrency = 3;
let logDir = resolve(projectRoot, "../../../.local/test-results/browser-parallel");
const specs = [];
for (let index = 0; index < args.length; index += 1) {
  if (args[index] === "--jobs") concurrency = Math.max(1, Number(args[++index]) || 1);
  else if (args[index] === "--logs") logDir = resolve(args[++index]);
  else specs.push(args[index]);
}
if (specs.length === 0) {
  throw new Error("No browser journeys given. Example: runBrowserGateParallel.mjs metric-fields localization=zh-CN,en-US");
}
await mkdir(logDir, { recursive: true });

const jobs = specs.map((spec) => {
  const [journey, languages] = spec.split("=");
  const env = { ...process.env, RM_BROWSER_JOURNEY: journey };
  if (languages) env.RM_LOCALIZATION_LANGUAGES = languages;
  else delete env.RM_LOCALIZATION_LANGUAGES;
  const name = languages ? `${journey}-${languages.replaceAll(",", "+")}` : journey;
  // 指定了截图目录时，每个任务用自己的子目录，避免并行任务互相覆盖。
  if (env.RM_BROWSER_ARTIFACT_DIR) env.RM_BROWSER_ARTIFACT_DIR = resolve(env.RM_BROWSER_ARTIFACT_DIR, name);
  return { name, env };
});

const results = [];
let next = 0;
async function worker() {
  while (next < jobs.length) {
    const job = jobs[next++];
    results.push(await run(job));
  }
}
await Promise.all(Array.from({ length: Math.min(concurrency, jobs.length) }, worker));

const failed = results.filter((result) => result.code !== 0);
for (const result of results) {
  process.stdout.write(`${result.code === 0 ? "PASS" : "FAIL"} ${result.name} ${(result.ms / 1000).toFixed(0)}s ${result.log}\n`);
}
process.exitCode = failed.length > 0 ? 1 : 0;

async function run(job) {
  const started = Date.now();
  if (job.env.RM_BROWSER_ARTIFACT_DIR) await mkdir(job.env.RM_BROWSER_ARTIFACT_DIR, { recursive: true });
  const log = resolve(logDir, `${job.name}.log`);
  let output = "";
  const code = await new Promise((resolveCode) => {
    const child = spawn(process.execPath, [resolve(projectRoot, "tests/browser/runBrowserGate.mjs")], {
      cwd: projectRoot,
      env: job.env,
      stdio: ["ignore", "pipe", "pipe"]
    });
    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.on("data", (chunk) => { output += chunk; });
    child.on("close", (exitCode) => resolveCode(exitCode ?? 1));
  });
  await writeFile(log, output, "utf8");
  return { name: job.name, code, ms: Date.now() - started, log };
}
