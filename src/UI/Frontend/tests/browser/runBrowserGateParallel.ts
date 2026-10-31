import { spawn } from "node:child_process";
import { createWriteStream } from "node:fs";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

/*
 * 兼容入口。原来这里给每个任务开一个 runBrowserGate 进程（各自一个浏览器和模拟服务器）；现在
 * runBrowserGate.ts 自己用几个工作进程并行，每个进程复用一个浏览器和模拟服务器，并把九语旅程按语言
 * 拆开，所以这里只把旧参数翻译成它的环境变量，跑一次。
 * 删除条件：.local 下的命令文件和项目文档都不再调用本脚本后删除。
 *
 *   node tests/browser/runBrowserGateParallel.ts [--jobs N] [--logs DIR] [--keep-going] 任务...
 *
 * 任务写法：
 *   旅程名                          例如 metric-field-cards
 *   localization=语言,语言          九语旅程只跑这几种语言（RM_LOCALIZATION_LANGUAGES；几个这样的任务合并）
 *
 * --jobs：工作进程数（RM_BROWSER_WORKERS）；不给就用 runBrowserGate 的默认值。
 * --keep-going：转交 runner，失败后仍覆盖剩余任务；失败仍以 1 退出。
 * --logs：完整输出写到 DIR/browser-gate.log，默认 .local/test-results/browser-parallel。
 * 调用前先 npm run build；本脚本不构建。屏幕上只输出每个任务一行结果和汇总，任一失败则以 1 退出。
 */

const projectRoot = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const args = process.argv.slice(2);
let workers: string | null = null;
let keepGoing = false;
let logDir = resolve(projectRoot, "../../../.local/test-results/browser-parallel");
const specs: string[] = [];
for (let index = 0; index < args.length; index += 1) {
  if (args[index] === "--jobs") workers = args[++index];
  else if (args[index] === "--keep-going") keepGoing = true;
  else if (args[index] === "--logs") logDir = resolve(args[++index]);
  else if (args[index] === "--keep-going") keepGoing = true;
  else specs.push(args[index]);
}
if (specs.length === 0) {
  throw new Error("No browser journeys given. Example: runBrowserGateParallel.ts metric-fields localization=zh-CN,en-US");
}
await mkdir(logDir, { recursive: true });

const journeys: string[] = [];
const languages: string[] = [];
for (const spec of specs) {
  const [journey, languageList] = spec.split("=");
  if (!journeys.includes(journey)) journeys.push(journey);
  for (const language of languageList?.split(",").map(id => id.trim()).filter(Boolean) ?? []) {
    if (!languages.includes(language)) languages.push(language);
  }
}
const env: NodeJS.ProcessEnv = { ...process.env, RM_BROWSER_JOURNEY: journeys.join(",") };
if (languages.length > 0) env.RM_LOCALIZATION_LANGUAGES = languages.join(",");
else delete env.RM_LOCALIZATION_LANGUAGES;
if (workers !== null) env.RM_BROWSER_WORKERS = workers;

const logPath = resolve(logDir, "browser-gate.log");
const log = createWriteStream(logPath);
const code = await new Promise<number>((resolveCode) => {
  // 与 package.json 的 test:browser 相同：旅程导入 .ts 源码，需要 --experimental-transform-types。
  const nodeFlags = process.execArgv.includes("--experimental-transform-types")
    ? process.execArgv : [...process.execArgv, "--experimental-transform-types"];
  const child = spawn(process.execPath, [...nodeFlags, resolve(projectRoot, "tests/browser/runBrowserGate.ts"),
    ...(keepGoing ? ["--keep-going"] : [])], {
    cwd: projectRoot,
    env,
    stdio: ["ignore", "pipe", "pipe"]
  });
  let pending = "";
  child.stdout.on("data", (chunk) => {
    log.write(chunk);
    pending += chunk;
    let newline = pending.indexOf("\n");
    while (newline >= 0) {
      const line = pending.slice(0, newline);
      if (/^(PASS|FAIL|FAILED|browser gate:|slowest jobs:|not run:)/.test(line)) process.stdout.write(`${line}\n`);
      pending = pending.slice(newline + 1);
      newline = pending.indexOf("\n");
    }
  });
  child.stderr.on("data", (chunk) => log.write(chunk));
  child.on("close", (exitCode) => resolveCode(exitCode ?? 1));
});
await new Promise<void>((resolveEnd) => log.end(resolveEnd));
process.stdout.write(`full output: ${logPath}\n`);
process.exitCode = code;
