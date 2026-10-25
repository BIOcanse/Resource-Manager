// 手感目录和 motion.css 一致：标准就是 :root 的令牌，其余手感各有一块 :root[data-motion-feel] 覆盖，
// 每块把目录里的令牌写全、值相同，--ease-bar 不随手感变。规则见 docs/motion_feel_2026-10-25.md。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { motionFeels } from "../src/presentation/motionFeel.ts";

const css = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../src/styles/motion.css"), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");

function block(selector: string): Map<string, string> {
  const start = css.indexOf(`${selector} {`);
  assert.ok(start >= 0, `motion.css has no block ${selector}`);
  assert.equal(css.indexOf(`${selector} {`, start + 1), -1, `motion.css repeats ${selector}`);
  const body = css.slice(css.indexOf("{", start) + 1, css.indexOf("}", start));
  const declarations = new Map<string, string>();
  for (const line of body.split(";")) {
    const colon = line.indexOf(":");
    if (colon >= 0) declarations.set(line.slice(0, colon).trim(), line.slice(colon + 1).trim());
  }
  return declarations;
}

const curve = (points: readonly number[]) => `cubic-bezier(${points.join(", ")})`;

assert.equal(motionFeels[0].id, "standard", "The first feel is the :root default");
assert.equal(new Set(motionFeels.map((feel) => feel.id)).size, motionFeels.length, "Feel IDs are unique");

for (const [index, feel] of motionFeels.entries()) {
  const declarations = block(index === 0 ? ":root" : `:root[data-motion-feel="${feel.id}"]`);
  for (const [token, ms] of Object.entries(feel.durationsMs)) {
    assert.equal(declarations.get(token), `${ms}ms`, `${feel.id} ${token}`);
  }
  for (const [token, points] of Object.entries(feel.easings)) {
    assert.equal(declarations.get(token), curve(points), `${feel.id} ${token}`);
  }
  if (index > 0) {
    assert.equal(declarations.size, Object.keys(feel.durationsMs).length + Object.keys(feel.easings).length,
      `${feel.id} overrides only the catalog tokens: ${[...declarations.keys()].join(", ")}`);
    assert.ok(!declarations.has("--ease-bar"), `${feel.id} must not change --ease-bar`);
  }
}

// 没有目录之外的手感块。
const feelSelectors = [...css.matchAll(/data-motion-feel="([^"]+)"/g)].map((match) => match[1]);
assert.deepEqual([...new Set(feelSelectors)].sort(), motionFeels.slice(1).map((feel) => feel.id).sort());

console.log(`motionFeelContract: ${motionFeels.length} feels match motion.css`);
