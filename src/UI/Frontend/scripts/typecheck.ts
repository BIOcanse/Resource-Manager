import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { delimiter, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript-api";

// Resolve genuine installed packages once for every project. NODE_PATH affects
// Node's loader, but tsc needs the same locations explicitly in compiler options.
const frontend = fileURLToPath(new URL("..", import.meta.url));
const repository = resolve(frontend, "../../..");
const frontendRequire = createRequire(join(frontend, "package.json"));
interface PackageInfo { name: string; version: string; types?: string; typings?: string }
interface InstalledPackage { manifest: string; directory: string; info: PackageInfo; declaration: string }
function git(args: string[]): string {
  const result = spawnSync("git", args, { cwd: repository, encoding: "utf8", windowsHide: true });
  return result.status === 0 ? result.stdout.trim() : "";
}
function mainCheckout(): string | undefined {
  let checkout: string | undefined;
  for (const line of git(["worktree", "list", "--porcelain"]).split(/\r?\n/)) {
    if (line.startsWith("worktree ")) checkout = line.slice("worktree ".length);
    if (line === "branch refs/heads/codex/development-mainline") return checkout;
  }
  return undefined;
}
const tools = process.env.RM_TOOLS_ROOT || git(["config", "--get", "rm.toolsRoot"]);
const runtimeModule = process.env.RM_PLAYWRIGHT_MODULE
  || (tools ? resolve(tools, "PlaywrightCLI/node_modules/playwright/index.mjs") : undefined);
const runtimeAnchor = runtimeModule?.startsWith("file:") ? fileURLToPath(runtimeModule) : runtimeModule;
const main = mainCheckout();
const anchors = [
  ...(runtimeAnchor ? [resolve(runtimeAnchor)] : []),
  ...(process.env.NODE_PATH?.split(delimiter).filter(Boolean).map(path => join(resolve(path), "../package.json")) ?? []),
  join(frontend, "package.json"),
  ...(main ? [join(main, "scripts/analysis/node/package.json")] : [])
];
function installedPackage(name: string, optional = false): InstalledPackage | undefined {
  for (const anchor of anchors) {
    let manifest: string;
    try { manifest = createRequire(anchor).resolve(`${name}/package.json`); }
    catch { continue; }
    const info = JSON.parse(readFileSync(manifest, "utf8")) as PackageInfo;
    const directory = dirname(manifest);
    const declaration = info.types ?? info.typings;
    if (!declaration || !existsSync(resolve(directory, declaration))) continue;
    return { manifest, directory, info, declaration: resolve(directory, declaration) };
  }
  if (optional) return undefined;
  throw new Error(`Typechecking needs the installed ${name} declarations. Install the frontend lock or supply its shared install through NODE_PATH/RM_PLAYWRIGHT_MODULE.`);
}
const core = installedPackage("playwright-core")!;
const playwright = installedPackage("playwright", true);
if (playwright && playwright.info.version !== core.info.version) throw new Error("Playwright and playwright-core must resolve from the same runtime version.");
const eslint = installedPackage("eslint")!;
const compilerApi = installedPackage("typescript-api")!;
const dependencyPaths: Record<string, string[]> = {
  "playwright-core": [core.declaration],
  "eslint": [eslint.declaration],
  "typescript": [compilerApi.declaration]
};
if (playwright) dependencyPaths.playwright = [playwright.declaration];
const configuredProjects = [
  "tsconfig.json", "tsconfig.tooling.json", "tests/browser/tsconfig.json",
  "tests/tsconfig.localization.json", "tests/tsconfig.task-registry.json"
];
const args = process.argv.slice(2);
const prepareOnly = args.includes("--prepare");
const selection = args.filter(arg => arg !== "--prepare");
if (selection.length && (selection[0] !== "--project" || selection.length !== 2)) throw new Error("Usage: typecheck.ts [--project path/to/tsconfig.json] [--prepare]");
const projects = selection.length ? [selection[1]!] : configuredProjects;
const owner = process.env.RM_LEDGER_WORK_ROOT;
if (!owner || !isAbsolute(owner)) throw new Error("Set RM_LEDGER_WORK_ROOT to this check's registered running ledger.");
const marker = JSON.parse(readFileSync(join(owner, ".generated-workspace.json"), "utf8")) as { state: string; workspacePath: string };
if (marker.state !== "running" || resolve(marker.workspacePath).toLowerCase() !== resolve(owner).toLowerCase()) throw new Error("Typecheck output requires a running registered ledger.");
const output = join(owner, "typecheck-projects");
mkdirSync(output, { recursive: true });
function parseProject(config: string): ts.ParsedCommandLine {
  const parsed = ts.getParsedCommandLineOfConfigFile(config, {}, {
    ...ts.sys,
    onUnRecoverableConfigFileDiagnostic(diagnostic) { throw new Error(ts.flattenDiagnosticMessageText(diagnostic.messageText, "\n")); }
  });
  if (!parsed || parsed.errors.length) throw new Error(parsed?.errors.map(error => ts.flattenDiagnosticMessageText(error.messageText, "\n")).join("\n") || `Cannot parse ${config}`);
  if (parsed.options.strict !== true) throw new Error(`Strict must remain enabled: ${config}`);
  return parsed;
}
function sourceFiles(directory: string): string[] {
  const files: string[] = [];
  const pending = [directory];
  while (pending.length) {
    const current = pending.pop()!;
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (entry.isDirectory() && entry.name !== "node_modules") pending.push(join(current, entry.name));
      else if (entry.isFile() && entry.name.endsWith(".ts")) files.push(join(current, entry.name));
    }
  }
  return files;
}
const conversionDirectories = [
  "tests/browser", "tests/realData", "scripts",
  "../../../scripts/analysis/node",
  "../../../tests/Validation/NonAdaptedOptimizationLab/GameBenchmarks/Analysis"
];
const convertedRootFiles = [
  "tests/fixtures/kernelLeakReports.ts", "tests/runContractSteps.ts",
  "tests/pageMotionTiming.test.ts", "tests/liveNativeUiDiagnostics.test.ts",
  "tests/interactionLongTaskBudget.test.ts"
];
const allConvertedFiles = [...conversionDirectories.flatMap(directory => sourceFiles(resolve(frontend, directory))), ...convertedRootFiles.map(file => resolve(frontend, file))];
const selectedProject = selection.length ? resolve(frontend, projects[0]!) : undefined;
const requiredFiles = !selectedProject || selectedProject === resolve(frontend, "tsconfig.tooling.json")
  ? allConvertedFiles
  : selectedProject === resolve(frontend, "tests/browser/tsconfig.json") ? sourceFiles(join(frontend, "tests/browser")) : [];
const coverage = new Set<string>();
const records: { project: string; generatedConfig: string; files: string[]; exitCode?: number }[] = [];
const compiler = resolve(dirname(frontendRequire.resolve("typescript/package.json")), "bin/tsc");
for (const project of projects) {
  const source = resolve(frontend, project);
  const parsed = parseProject(source);
  const pathsBase = typeof parsed.options.pathsBasePath === "string" ? parsed.options.pathsBasePath : dirname(source);
  const paths: Record<string, string[]> = Object.fromEntries(Object.entries(parsed.options.paths ?? {}).map(([name, targets]) =>
    [name, targets.map(target => resolve(pathsBase, target))]));
  const types = (parsed.options.types ?? []).map(name => {
    const result = ts.resolveTypeReferenceDirective(name, source, parsed.options, ts.sys).resolvedTypeReferenceDirective;
    if (!result) throw new Error(`Cannot resolve real type library ${name} for ${source}`);
    return result.resolvedFileName;
  });
  const generatedConfig = join(output, relative(frontend, source).replaceAll(/[\\/]/g, "__"));
  writeFileSync(generatedConfig, JSON.stringify({ extends: source, compilerOptions: {
    paths: { ...paths, ...dependencyPaths }, types,
    noEmit: true, noUnusedLocals: true, noUnusedParameters: true
  } }, null, 2) + "\n");
  // Use the actual gate compiler's program, not just a second parser's glob list.
  const listing = spawnSync(process.execPath, [compiler, "--project", generatedConfig, "--listFilesOnly"], {
    cwd: frontend, encoding: "utf8", windowsHide: true
  });
  if (listing.error) throw listing.error;
  if (listing.status !== 0) throw new Error(`tsc preparation failed for ${source}:\n${listing.stdout}\n${listing.stderr}`);
  const files = listing.stdout.split(/\r?\n/).filter(file => isAbsolute(file) && existsSync(file));
  files.forEach(file => coverage.add(resolve(file).toLowerCase()));
  records.push({ project: source, generatedConfig, files });
}
const trackedProjects = git(["ls-files", "src/UI/Frontend/*tsconfig*.json", "src/UI/Frontend/**/tsconfig*.json"])
  .split(/\r?\n/).filter(Boolean).map(project => resolve(repository, project));
if (!selection.length) {
  const omittedProjects = trackedProjects.filter(project => !records.some(record => record.project === project));
  if (omittedProjects.length) throw new Error(`Typecheck projects missing from the gate:\n${omittedProjects.join("\n")}`);
}
const missing = requiredFiles.filter(file => !coverage.has(resolve(file).toLowerCase()));
if (missing.length) throw new Error(`Converted TypeScript files missing from the strict gate:\n${missing.join("\n")}`);
const evidence = { packages: { playwrightCore: core, playwright, eslint, compilerApi }, requiredFiles, missing, projects: records };
const evidencePath = join(output, selectedProject
  ? selectedProject === resolve(frontend, "tests/browser/tsconfig.json") ? "browser-coverage.json" : "single-project-coverage.json"
  : "all-coverage.json");
writeFileSync(evidencePath, JSON.stringify(evidence, null, 2) + "\n");
console.log(`Strict coverage: ${requiredFiles.length} required files, ${missing.length} missing; ${records.length} project(s).`);
console.log(`Playwright types: ${core.declaration}`);
let failed = false;
for (const record of records) {
  console.log(`tsc: ${relative(frontend, record.project)}`);
  if (prepareOnly) continue;
  const result = spawnSync(process.execPath, [compiler, "--project", record.generatedConfig], { cwd: frontend, stdio: "inherit", windowsHide: true });
  if (result.error) throw result.error;
  record.exitCode = result.status ?? 1;
  if (record.exitCode !== 0) failed = true;
}
writeFileSync(evidencePath, JSON.stringify(evidence, null, 2) + "\n");
process.exitCode = failed ? 1 : 0;
