// typescript-api is the 6.x compiler under an alias: TypeScript 7.x ships no JavaScript compiler API (its package exports
// only the version). Remove the alias when 7.x ships a stable compiler API: port this file to it and drop typescript-api.
import ts from "typescript-api";
import { realpathSync, existsSync, writeFileSync } from "node:fs";
import { resolve, dirname, relative, join } from "node:path";
import { createRequire } from "node:module";

const root = resolve(import.meta.dirname, "..");
const dependencyRoot = realpathSync(join(root, "node_modules"));
const configPath = join(root, "tsconfig.json");
const config = ts.readConfigFile(configPath, ts.sys.readFile);
if (config.error) throw new Error(ts.flattenDiagnosticMessageText(config.error.messageText, "\n"));
const rawConfig = config.config as { compilerOptions?: Record<string, unknown> };
const parsed = ts.parseJsonConfigFileContent(rawConfig, ts.sys, root, undefined, configPath);
if (parsed.errors.length) throw new Error(parsed.errors.map(error => ts.flattenDiagnosticMessageText(error.messageText, "\n")).join("\n"));
const program = ts.createProgram(parsed.fileNames, parsed.options);
// The worktree's node_modules is a junction. Export the real compiler's resolved
// module/type filenames so the SDK's closed file host does not need to follow it.
// No declaration or dependency is fabricated; verify the same physical source set.
const paths: Record<string, [string]> = {};
for (const source of program.getSourceFiles()) {
  // The compiler populates imports while constructing the program, including its
  // implicit imports. The property is internal; its entries are real compiler AST nodes.
  const compilerSource = source as ts.SourceFile & { readonly imports?: readonly ts.StringLiteralLike[] };
  for (const specifier of compilerSource.imports ?? []) {
    const name = specifier.text;
    if (name.startsWith(".") || name.startsWith("/") || name.startsWith("node:")) continue;
    const result = ts.resolveModuleName(name, source.fileName, parsed.options, ts.sys).resolvedModule;
    if (!result) continue;
    const file = realpathSync(result.resolvedFileName);
    if (paths[name] && paths[name][0] !== file) throw new Error(`Ambiguous captured module ${name}`);
    paths[name] = [file];
  }
}
const types = (parsed.options.types ?? []).map(name => {
  const result = ts.resolveTypeReferenceDirective(name, configPath, parsed.options, ts.sys).resolvedTypeReferenceDirective;
  if (!result) throw new Error(`Unresolved original type library ${name}`);
  return realpathSync(result.resolvedFileName!);
});
const exportedConfig = { compilerOptions: { ...rawConfig.compilerOptions, paths, types }, files: parsed.fileNames };
const exported = ts.parseJsonConfigFileContent(exportedConfig, ts.sys, root);
const resolvedProgram = ts.createProgram(exported.fileNames, exported.options);
const sourceSet = (value: ts.Program) => [...new Set(value.getSourceFiles().map(source => realpathSync(source.fileName)))].sort();
if (JSON.stringify(sourceSet(program)) !== JSON.stringify(sourceSet(resolvedProgram))) throw new Error("Captured compiler source set changed");
const diagnostics = ts.getPreEmitDiagnostics(resolvedProgram).filter(item => item.category === ts.DiagnosticCategory.Error);
if (diagnostics.length) throw new Error(diagnostics.map(item => ts.flattenDiagnosticMessageText(item.messageText, "\n")).join("\n"));
writeFileSync(process.argv[3] ? resolve(process.argv[3]) : join(dirname(resolve(process.argv[2]!)), "generation", "frontend.tsconfig.json"), JSON.stringify(exportedConfig, null, 2));
const files = new Set<string>();
for (const source of program.getSourceFiles()) {
  const physical = realpathSync(source.fileName);
  const dependency = relative(dependencyRoot, physical);
  if (dependency.startsWith("..") || dependency === "") continue;
  files.add(dependency.replaceAll("\\", "/"));
  let directory = dirname(physical);
  for (let depth = 0; depth < 32 && directory !== dependencyRoot; depth++) {
    const manifest = join(directory, "package.json");
    if (existsSync(manifest)) files.add(relative(dependencyRoot, manifest).replaceAll("\\", "/"));
    const parent = dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
}
const require = createRequire(import.meta.url);
writeFileSync(resolve(process.argv[2]!), JSON.stringify({
  nodeExecutable: process.execPath, nodeVersion: process.version,
  typeScriptModule: require.resolve("typescript-api"), typeScriptVersion: ts.version,
  dependencyRoot, contextFiles: [...files].sort()
}, null, 2));
console.log(`Captured ${files.size} real TypeScript dependency/context files.`);
