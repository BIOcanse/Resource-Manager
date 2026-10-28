import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { delimiter, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/*
 * 并行运行一个 npm 脚本展开后的全部契约步骤。
 *
 *   node tests/runContractSteps.mjs [--jobs N] [--list] [--keep-going] <脚本名>
 *
 * --list：只打印展开后的步骤（每行：步骤名、所属脚本、命令），不运行。
 * --keep-going：一个步骤失败后照样启动其余步骤（STEP_ORDER 里排在失败步骤之后的除外），最后列出全部失败；
 *   不给时第一个失败之后不再启动新步骤。npm 里写成 npm run test:contracts -- --keep-going。
 *
 * 展开：脚本按 "&&" 切成步骤，"npm run X" 换成 X 的步骤，按原顺序排成一张表。命令文字完全相同的
 * 步骤只跑一次（第一次出现的位置），输出里列出它属于的所有脚本。test:contracts 用它跑
 * test:contract-steps；把新的契约脚本加进 test:contract-steps 的链即可，不用改这里。
 *
 * 并发：同时运行 DEFAULT_CONTRACT_JOBS 个步骤；--jobs N 或环境变量 RM_CONTRACT_JOBS 覆盖，1 就是串行。
 *
 * 顺序：步骤之间默认互不依赖，可以任意并行。要求先后的步骤写进 STEP_ORDER（下面写明当前为什么是空的）。
 *
 * 输出：每个步骤的输出先缓存，结束后连同步骤名、所属脚本、用时一起打印，不会交错。默认第一个失败的步骤
 * 之后不再启动新步骤（与原来 "&&" 链的停止语义一致），已在运行的步骤跑完；--keep-going 时继续。最后列出失败
 * 步骤的名字和退出码，以及没有启动的步骤数，以 1 退出。
 */

const DEFAULT_CONTRACT_JOBS = 4;

/*
 * 显式的先后依赖：{ step, after }，step 和 after 都是步骤名（见 stepName：node 步骤是它运行的文件，
 * 其他步骤是整条命令）。step 等 after 里的步骤全部成功后才启动。
 *
 * 当前为空。2026-10-26 核对 dev 33425ec5 的 175 次展开、172 个不同步骤：只有 localizationCoverage.test.ts
 * 写文件（.local/test-results/localization-coverage.json，链里没有步骤读它）；两个 tsc 步骤的
 * tsconfig 继承 noEmit；没有步骤监听固定端口。test:html 读 vite 构建输出，构建在契约之前单独运行，
 * 不属于这条链。新增的五个 mock 契约各用独立进程和随机端口，另外十一个新增契约只读或在内存中处理。
 * 新增读写共享文件或构建输出的步骤时在这里声明。
 */
const STEP_ORDER = [];

const projectRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const args = process.argv.slice(2);
let jobs = Number(process.env.RM_CONTRACT_JOBS || DEFAULT_CONTRACT_JOBS);
let scriptName = null;
let listOnly = false;
let keepGoing = false;
for (let index = 0; index < args.length; index += 1) {
  if (args[index] === "--jobs") jobs = Number(args[++index]);
  else if (args[index] === "--list") listOnly = true;
  else if (args[index] === "--keep-going") keepGoing = true;
  else if (scriptName === null) scriptName = args[index];
  else throw new Error(`Unexpected argument: ${args[index]}`);
}
if (!Number.isInteger(jobs) || jobs < 1) throw new Error(`The job count must be a positive integer: ${jobs}`);
if (!scriptName) throw new Error("Usage: runContractSteps.mjs [--jobs N] [--list] [--keep-going] <npm script>");

const scripts = JSON.parse(readFileSync(resolve(projectRoot, "package.json"), "utf8")).scripts;
const steps = expandScript(scripts, scriptName);
const order = resolveStepOrder(steps, STEP_ORDER);
if (listOnly) {
  for (const step of steps) process.stdout.write(`${step.name}\t${step.scripts.join(",")}\t${step.command}\n`);
} else {
  const started = Date.now();
  const results = await runSteps(steps, order, jobs);
  printSummary(steps, results, Date.now() - started);
}

function expandScript(allScripts, rootName) {
  // 显式栈按原顺序展开；同一条 npm run 链上重复进入同一脚本视为环。
  const ordered = [];
  const byCommand = new Map();
  const stack = [{ name: rootName, parts: splitScript(allScripts, rootName), index: 0, chain: [rootName] }];
  const maximumSteps = 10_000;
  let expandedParts = 0;
  while (stack.length > 0) {
    const frame = stack[stack.length - 1];
    if (frame.index >= frame.parts.length) {
      stack.pop();
      continue;
    }
    const part = frame.parts[frame.index++];
    if (++expandedParts > maximumSteps) throw new Error(`More than ${maximumSteps} expanded contract parts`);
    const reference = part.match(/^npm run (\S+)$/);
    if (reference) {
      const child = reference[1];
      if (frame.chain.includes(child)) throw new Error(`npm run cycle: ${[...frame.chain, child].join(" -> ")}`);
      stack.push({ name: child, parts: splitScript(allScripts, child), index: 0, chain: [...frame.chain, child] });
      continue;
    }
    const existing = byCommand.get(part);
    if (existing) {
      if (!existing.scripts.includes(frame.name)) existing.scripts.push(frame.name);
      existing.occurrences += 1;
      continue;
    }
    const step = { index: ordered.length, command: part, name: stepName(part), scripts: [frame.name], occurrences: 1 };
    byCommand.set(part, step);
    ordered.push(step);
    if (ordered.length > maximumSteps) throw new Error(`More than ${maximumSteps} contract steps`);
  }
  return ordered;
}

function splitScript(allScripts, name) {
  const body = allScripts[name];
  if (typeof body !== "string") throw new Error(`Unknown npm script: ${name}`);
  return body.split("&&").map((part) => part.trim()).filter(Boolean);
}

function stepName(command) {
  const tokens = command.split(/\s+/);
  if (tokens[0] !== "node") return command;
  return tokens.slice(1).filter((token) => !token.startsWith("-")).join(" ");
}

function resolveStepOrder(allSteps, declarations) {
  const byName = new Map(allSteps.map((step) => [step.name, step]));
  const prerequisites = new Map(allSteps.map((step) => [step, []]));
  for (const { step, after } of declarations) {
    const target = byName.get(step);
    if (!target) throw new Error(`STEP_ORDER names an unknown step: ${step}`);
    for (const name of after) {
      const prerequisite = byName.get(name);
      if (!prerequisite) throw new Error(`STEP_ORDER names an unknown prerequisite: ${name}`);
      if (prerequisite.index >= target.index) throw new Error(`STEP_ORDER: ${name} must come before ${step} in the chain`);
      prerequisites.get(target).push(prerequisite);
    }
  }
  return prerequisites;
}

async function runSteps(allSteps, prerequisites, concurrency) {
  const results = new Map();
  const pending = [...allSteps];
  const running = new Map();
  let stopped = false;
  let finished = 0;
  const binPath = resolve(projectRoot, "node_modules", ".bin");
  const env = { ...process.env, PATH: `${binPath}${delimiter}${process.env.PATH ?? process.env.Path ?? ""}` };
  delete env.Path;
  while (pending.length > 0 || running.size > 0) {
    while (!stopped && running.size < concurrency) {
      const readyIndex = pending.findIndex((step) => prerequisites.get(step).every((before) => results.get(before)?.code === 0));
      if (readyIndex < 0) break;
      const [step] = pending.splice(readyIndex, 1);
      running.set(step, runOne(step, env).then(result => ({ step, result })));
    }
    if (running.size === 0) break;
    const { step, result } = await Promise.race(running.values());
    running.delete(step);
    results.set(step, result);
    finished += 1;
    printStep(step, result, finished, allSteps.length);
    if (result.code !== 0 && !keepGoing) stopped = true;
  }
  return results;
}

function runOne(step, env) {
  const begin = Date.now();
  return new Promise((resolveStep) => {
    const chunks = [];
    const child = spawn(step.command, { cwd: projectRoot, env, shell: true, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    child.stdout.on("data", (chunk) => chunks.push(chunk));
    child.stderr.on("data", (chunk) => chunks.push(chunk));
    child.on("error", (error) => chunks.push(Buffer.from(`${error.stack ?? error}\n`)));
    child.on("close", (code, signal) => resolveStep({
      code: code ?? 1,
      signal,
      ms: Date.now() - begin,
      output: Buffer.concat(chunks).toString("utf8")
    }));
  });
}

function printStep(step, result, finished, total) {
  const status = result.code === 0 ? "PASS" : "FAIL";
  const owners = step.scripts.join(", ");
  const exit = result.code === 0 ? "" : ` exit ${result.code}${result.signal ? ` (${result.signal})` : ""}`;
  let block = `--- ${status} ${step.name} [${owners}] ${(result.ms / 1000).toFixed(1)}s${exit} (${finished}/${total})\n`;
  if (result.output) block += result.output.endsWith("\n") ? result.output : `${result.output}\n`;
  process.stdout.write(block);
}

function printSummary(allSteps, results, wallMs) {
  const failed = allSteps.filter((step) => results.has(step) && results.get(step).code !== 0);
  const notRun = allSteps.filter((step) => !results.has(step));
  const duplicates = allSteps.filter((step) => step.occurrences > 1);
  const slowest = [...results.entries()].sort((left, right) => right[1].ms - left[1].ms).slice(0, 10);
  const lines = [
    `contract steps: ${allSteps.length} from ${scriptName}, ${results.size} run, ${jobs} at a time, ${(wallMs / 1000).toFixed(1)}s wall`,
    `duplicate commands run once: ${duplicates.length === 0 ? "none" : duplicates.map((step) => `${step.name} [${step.scripts.join(", ")}]`).join("; ")}`,
    `slowest: ${slowest.map(([step, result]) => `${step.name} ${(result.ms / 1000).toFixed(1)}s`).join("; ")}`
  ];
  for (const step of failed) {
    const result = results.get(step);
    lines.push(`FAILED ${step.name} [${step.scripts.join(", ")}] exit ${result.code}`);
  }
  if (failed.length > 0 && notRun.length > 0) {
    lines.push(keepGoing ? `not started because a step they wait for failed: ${notRun.length} steps`
      : `not started after the failure: ${notRun.length} steps (--keep-going runs them)`);
  }
  process.stdout.write(`${lines.join("\n")}\n`);
  process.exitCode = failed.length > 0 || notRun.length > 0 ? 1 : 0;
}
