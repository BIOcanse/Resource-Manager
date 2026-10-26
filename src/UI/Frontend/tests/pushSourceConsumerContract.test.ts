import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join, relative } from "node:path";

const root = fileURLToPath(new URL("../src/", import.meta.url));
const pending = [root];
const violations: string[] = [];
let visited = 0;
while (pending.length) {
  assert(++visited < 10000, "Frontend source traversal stays bounded");
  const directory = pending.pop()!;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (directory === root && entry.name === "frontendRuntime") continue;
    if (entry.isDirectory()) pending.push(path);
    else if (/\.[cm]?[jt]sx?$/.test(entry.name) && /\.subscribeSnapshot\s*\(/.test(readFileSync(path, "utf8"))) {
      violations.push(relative(root, path));
    }
  }
}
assert.deepEqual(violations, [], "Frontend consumers use runtime bindings, never direct snapshot subscriptions");
console.log("Push consumer contract: direct snapshot subscriptions stay inside frontendRuntime.");
