import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { getDiskUsageTree } from "../src/features/diskUsage/diskUsageApi.ts";

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
assert.match(workspace, /getDiskUsageTree\(runtime\.requestClient, signal, rootIndex/);
assert.match(workspace, /layoutWorker!\.replaceTree\(treeBuffer\)/);
assert.match(workspace, /layoutWorker!\.layout\(rootNodeId, view\)/);
assert.match(workspace, /layoutWorker\?\.dispose\(\)/);
assert.doesNotMatch(layoutApi, /\/api\/disk-usage\/layout/);
assert.doesNotMatch(layoutApi, /parameters\.set\("(?:pixelWidth|pixelHeight|scale|minX|minY|maxX|maxY)"/);
assert.match(layoutApi, /parameters\.set\("rootIndex", String\(rootIndex\)\)/);
assert.match(resultPanel, /props\.summary\.roots\.length > 1/);
assert.match(resultPanel, /uiText\.diskUsage\.rootSelectorLabel/);
assert.match(resultPanel, /props\.summary\.fileCount/);
assert.match(resultPanel, /props\.layout\.rootSizeBytes/);
assert.match(resultPanel, /props\.queryPartial \|\| props\.hasPreviousPage/);
assert.match(resultPanel, /uiText\.diskUsage\.queryPage\(props\.queryPageNumber\)/);
assert.match(resultPanel, /disabled=\{!props\.hasNextPage \|\| props\.queryBusy\}/);
assert.match(resultPanel, /disabled=\{!props\.hasPreviousPage \|\| props\.queryBusy\}/);


// Failure is independent of data: retain old results, label them stale and retry the failed selection.
assert.match(workspace, /kind: "layout", retry: \(\) => requestLayout\(rootNodeId, view, reset, nextDrillStack\)/);
assert.match(workspace, /refreshResult\(workspaceController\?\.signal, nodeId, rootIndex, cursor/);
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

assert.match(workspace, /if \(!current \|\| resultReadFailure\(\) \|\| queryBusy\(\)/);
assert.match(workspace, /requestedLayoutRoot !== null && requestedLayoutRoot !== current\.rootNodeId/);
assert.match(workspace, /failure\?\.kind === "result" \? failure : null/);

assert.match(workspace, /createSignal<DiskUsageScanSummary \| null \| undefined>\(undefined\)/);
assert.match(workspace, /summary\(\) === undefined \|\| layout\(\) === undefined \? "loading" : "ready"/);
assert.match(page, /!props\.hasResult && props\.resultReadState !== "error"/);
assert.match(page, /kind=\{props\.resultReadState === "loading" \? "waiting" : "empty"\}/);
assert.match(page, /props\.resultReadState === "loading" \? undefined : uiText\.diskUsage\.emptyDetail/);

const queries: URLSearchParams[] = [];
await getDiskUsageTree({ request: async request => {
  queries.push(new URL(request.url, "http://fixture").searchParams);
  return new ArrayBuffer(64) as never;
} }, undefined, 1, 3, 4);
assert.equal(queries.length, 1);
assert.deepEqual(Object.fromEntries(queries[0]), {
  rootIndex: "1", maxNodes: "400000", maxBytes: "33554432", node: "3", cursor: "4"
});
const english = (await loadAppCopy("en-US")).diskUsage;
for (const language of languageOptions.filter(option => option.id !== "system")) {
  const copy = (await loadAppCopy(language.id)).diskUsage;
  for (const field of ["partialQuery", "nextPage", "previousPage"] as const) {
    assert.equal(typeof copy[field], "string", `${language.id}.${field}`);
    assert.ok(copy[field].trim(), `${language.id}.${field} is empty`);
    if (language.id !== "en-US") assert.notEqual(copy[field], english[field], `${language.id}.${field} inherited English`);
  }
  assert.ok(copy.queryPage(2).includes("2"), `${language.id}: page number is missing`);
  assert.notEqual(copy.queryPage(1), copy.queryPage(2), `${language.id}: pagination copy ignored the page`);
}
console.log("diskUsageWorkspaceContract: ok");
