import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { resolve, relative } from "node:path";
import { fileURLToPath } from "node:url";

// Fixtures live under tests, outside the production source tree.
// One pending exception: before the bars subscription exists, the frontend has no typed way to ask for the
// physical memory residual's counter leaves, so resourceBarSources.ts names that software id in processDetails.
// Backend requirement filed 2026-10-26 (a metric-based residual detail selector on the bars/details subscription);
// delete this entry and the constant when it lands.
const pendingBackendSelector = "features/resourceBreakdown/resourceBarSources.ts";
const root = fileURLToPath(new URL("../src/", import.meta.url));
const pending = [root];
const violations: string[] = [];
let files = 0;
while (pending.length > 0) {
  const directory = pending.pop()!;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) pending.push(path);
    else if (entry.isFile()) {
      assert.ok(++files <= 50_000, "source scan must remain bounded");
      const lines = readFileSync(path, "utf8").split(/\r?\n/);
      for (const [index, line] of lines.entries()) {
        if (line.includes("resource-residual:") && relative(root, path).replaceAll("\\", "/") !== pendingBackendSelector) violations.push(`${relative(root, path)}:${index + 1}`);
      }
    }
  }
}
const pendingLines = readFileSync(resolve(root, pendingBackendSelector), "utf8").split(/\r?\n/).filter((line) => line.includes("resource-residual:"));
assert.equal(pendingLines.length, 1, `The pending backend selector exception covers exactly the one constant: ${pendingLines.join("\n")}`);
assert.deepEqual(violations, [], `Software IDs are opaque; production source must use typed residual identity/context:\n${violations.join("\n")}`);
console.log(`Opaque residual ID contract passed (${files} source files).`);
