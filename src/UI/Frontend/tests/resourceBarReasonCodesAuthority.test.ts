import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  resourceBarHardwareSamplePrefix,
  resourceBarUnavailableReasonFamilies
} from "../src/features/resourceBreakdown/resourceBarUnavailableReason.ts";

// 只在开发工作副本里跑的权威核对（npm run test:reason-codes-authority [-- --doc <路径>]），不进 test:contracts：
// docs/ 不进仓库（.gitignore 的 /docs/*），这份文档只在开发工作副本里有。
// 解析 docs/runtime_process_attribution_and_io_network_telemetry.md 的「Resource bar and fact unavailable reasons」一节，
// 要求前端的原因对应表和它列出的代码完全一致（不多不少），带参数的家族就是 hardware-sample-{ExceptionTypeName}。
// 默认路径是 <仓库>/docs/runtime_process_attribution_and_io_network_telemetry.md；--doc 显式指定另一份；找不到就失败并写出路径。

const args = process.argv.slice(2);
const docIndex = args.indexOf("--doc");
const repository = fileURLToPath(new URL("../../../../", import.meta.url));
const docPath = docIndex >= 0
  ? resolve(args[docIndex + 1] ?? "")
  : resolve(repository, "docs", "runtime_process_attribution_and_io_network_telemetry.md");
assert.ok(existsSync(docPath), `The reason authority document is missing: ${docPath} (docs/ is not committed; pass --doc <path> to the dev working copy's file)`);

const text = readFileSync(docPath, "utf8");
const heading = "## Resource bar and fact unavailable reasons";
const start = text.indexOf(heading);
assert.ok(start >= 0, `${docPath}: section "${heading}" not found`);
const next = text.indexOf("\n## ", start + heading.length);
const section = text.slice(start, next < 0 ? text.length : next);

// 表格里每一行的第一格是代码（反引号包着）；带 {…} 的是带参数的家族。
const literal: string[] = [];
const parameterized: string[] = [];
for (const line of section.split(/\r?\n/)) {
  const match = /^\|\s*`([^`]+)`\s*\|/.exec(line);
  if (!match) continue;
  (match[1].includes("{") ? parameterized : literal).push(match[1]);
}
const stated = /\*\*(\d+) entries \((\d+) literal codes and (\w+) parameterized famil/.exec(section);
assert.ok(stated, `${docPath}: the section states its entry count`);
assert.equal(literal.length + parameterized.length, Number(stated[1]), "parsed entries match the stated total");
assert.equal(literal.length, Number(stated[2]), "parsed literal codes match the stated count");
assert.equal(new Set(literal).size, literal.length, "no literal code is listed twice");

assert.deepEqual([...literal].sort(), Object.keys(resourceBarUnavailableReasonFamilies).sort(),
  "the frontend reason table lists exactly the documented literal codes");
assert.deepEqual(parameterized.map((family) => family.slice(0, family.indexOf("{"))), [resourceBarHardwareSamplePrefix],
  "the only parameterized family is the one the frontend handles");

console.log(`reason codes authority: ${literal.length} codes and ${parameterized.length} family match ${docPath}`);
