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
assert.match(workspace, /subscribeInvalidations\(/);
assert.match(workspace, /invalidation\.kind !== "disk-usage\.scan"/);
assert.match(workspace, /invalidation\.targets\.includes\("diskUsage"\)/);
assert.match(workspace, /operation\?\.state !== "succeeded"/);
assert.doesNotMatch(workspace, /waitForTerminal\(/);
assert.doesNotMatch(workspace, /const \[scanning, setScanning\]/);
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


// Failure is independent of data: retain old results, label them stale and retry the failed selection.
assert.match(workspace, /kind: "layout", retry: \(\) => requestLayout\(rootNodeId, view\)/);
assert.match(workspace, /refreshResult\(workspaceController\?\.signal, nodeId, rootIndex\)/);
assert.match(workspace, /cancelled\(cause, signal\)/);
assert.match(workspace, /cause instanceof Error && cause.name === "AbortError"/);
assert.match(workspace, /setResultReadFailure\(null\)/);
assert.match(workspace, /uiText\.diskUsage\.retainedResultStale/);
assert.match(workspace, /onClick=\{\(\) => failure\(\)\.retry\(\)\}/);
assert.doesNotMatch(workspace, /catch\([^)]*\) =>[^;]*setVolumes\(\[\]\)/);
const page = readFileSync(new URL("../src/features/diskUsage/DiskUsagePage.tsx", import.meta.url), "utf8");
assert.match(page, /props\.volumesReadState === "error"/);
assert.match(page, /props\.volumesReadState === "ready"/);
assert.match(page, /uiText\.diskUsage\.volumesReadFailed/);
assert.match(page, /uiText\.diskUsage\.retainedVolumesStale/);
assert.match(page, /onClick=\{props\.onRetryVolumes\}/);

assert.match(workspace, /if \(!current \|\| resultReadFailure\(\)\) \{/);
assert.match(workspace, /failure\?\.kind === "result" \? failure : null/);

console.log("diskUsageWorkspaceContract: ok");

assert.match(workspace, /createSignal<DiskUsageScanSummary \| null \| undefined>\(undefined\)/);
assert.match(workspace, /summary\(\) === undefined \|\| layout\(\) === undefined \? "loading" : "ready"/);
assert.match(page, /!props\.hasResult && props\.resultReadState !== "error"/);
assert.match(page, /kind=\{props\.resultReadState === "loading" \? "waiting" : "empty"\}/);
assert.match(page, /props\.resultReadState === "loading" \? undefined : uiText\.diskUsage\.emptyDetail/);
