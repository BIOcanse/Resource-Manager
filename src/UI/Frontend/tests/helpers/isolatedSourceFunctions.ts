import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
// typescript-api is the 6.x compiler under an alias: TypeScript 7.x ships no JavaScript compiler API (its package exports
// only the version). Remove the alias when 7.x ships a stable compiler API: port this file to it and drop typescript-api.
import ts from "typescript-api";

// Exercise pure functions embedded in TSX without loading the page or its UI runtime.
export function isolatedSourceFunctions(
  file: URL,
  names: readonly string[],
  bindings: Record<string, unknown> = {}
): Record<string, (...args: any[]) => any> {
  const source = readFileSync(file, "utf8");
  const tree = ts.createSourceFile(file.pathname, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const pending: ts.Node[] = [tree];
  const found = new Map<string, string>();
  while (pending.length) {
    const node = pending.pop()!;
    if (ts.isFunctionDeclaration(node) && node.name && names.includes(node.name.text)) {
      found.set(node.name.text, node.getText(tree).replace(/^export\s+/, ""));
    }
    ts.forEachChild(node, child => { pending.push(child); });
  }
  for (const name of names) assert.ok(found.has(name), `${file.pathname}: missing function ${name}`);
  const compiled = ts.transpileModule([...found.values()].join("\n"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
  }).outputText;
  return runInNewContext(`${compiled}\n({ ${names.join(", ")} })`, bindings);
}
