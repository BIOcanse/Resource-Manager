import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const workspace = readFileSync(
  new URL("../src/features/diskUsage/DiskUsageWorkspace.tsx", import.meta.url),
  "utf8");
const layoutApi = readFileSync(
  new URL("../src/features/diskUsage/diskUsageApi.ts", import.meta.url),
  "utf8");
const resultPanel = readFileSync(
  new URL("../src/features/diskUsage/DiskUsageResultPanel.tsx", import.meta.url),
  "utf8");

assert.match(workspace, /import \{ uiText \} from "\.\.\/\.\.\/text\.ts"/);
assert.match(workspace, /const \[scanFailed, setScanFailed\] = createSignal\(false\)/);
assert.match(workspace, /completed\.state !== "succeeded"/);
assert.match(workspace, /catch \{\s*setScanFailed\(true\);\s*\}/);
assert.match(workspace, /<p role="alert">\{uiText\.diskUsage\.scanFailed\}<\/p>/);
assert.doesNotMatch(workspace, /catch \{\s*\/\/ 失败的原因已经进了任务中心/);
assert.match(workspace, /const roots = summary\(\)\?\.roots/);
assert.match(workspace, /nodeId === undefined \? rootIndex : undefined/);
assert.match(layoutApi, /parameters\.set\("rootIndex", String\(rootIndex\)\)/);
assert.match(resultPanel, /props\.summary\.roots\.length > 1/);
assert.match(resultPanel, /uiText\.diskUsage\.rootSelectorLabel/);
assert.match(resultPanel, /props\.summary\.fileCount/);
assert.match(resultPanel, /props\.layout\.rootSizeBytes/);

console.log("diskUsageWorkspaceContract: ok");
