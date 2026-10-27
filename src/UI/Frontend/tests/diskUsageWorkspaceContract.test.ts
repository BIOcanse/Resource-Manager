import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { getDiskUsageTree } from "../src/features/diskUsage/diskUsageApi.ts";

const workspace = readFileSync(
  new URL("../src/features/diskUsage/DiskUsageWorkspace.tsx", import.meta.url),
  "utf8");
const owner = readFileSync(new URL("../src/features/diskUsage/diskUsageOwner.ts", import.meta.url), "utf8");
const disk = workspace + owner;
const layoutApi = readFileSync(
  new URL("../src/features/diskUsage/diskUsageApi.ts", import.meta.url),
  "utf8");
const resultPanel = readFileSync(
  new URL("../src/features/diskUsage/DiskUsageResultPanel.tsx", import.meta.url),
  "utf8");

assert.match(disk, /import \{ uiText \} from "\.\.\/\.\.\/text\.ts"/);
assert.match(disk, /const \[scanFailed, setScanFailed\] = createSignal\(false\)/);
assert.match(disk, /subscribeInvalidations\(/);
assert.match(disk, /invalidation\.kind !== "disk-usage\.scan"/);
assert.match(disk, /invalidation\.targets\.includes\("diskUsage"\)/);
assert.match(disk, /operation\?\.state !== "succeeded"/);
assert.doesNotMatch(disk, /waitForTerminal\(/);
assert.doesNotMatch(disk, /const \[scanning, setScanning\]/);
assert.match(disk, /catch \{\s*setScanFailed\(true\);\s*\}/);
assert.match(disk, /<p role="alert">\{uiText\.diskUsage\.scanFailed\}<\/p>/);
assert.doesNotMatch(disk, /catch \{\s*\/\/ 失败的原因已经进了任务中心/);
assert.match(disk, /const roots = summary\(\)\?\.roots/);
assert.match(disk, /getDiskUsageTree\(runtime\.requestClient, signal, query\.rootIndex/);
assert.match(disk, /candidate\.replaceTree\(treeBuffer\.slice\(0\)\)/);
assert.match(disk, /layoutWorker!\.layout\(rootNodeId, view\)/);
assert.match(disk, /layoutWorker\?\.dispose\(\)/);
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
assert.match(disk, /kind: "layout", retry: \(\) => requestLayout\(rootNodeId, view, reset, nextDrillStack\)/);
assert.match(disk, /refreshResult\(workspaceController\?\.signal, nodeId, rootIndex, cursor/);
assert.match(disk, /cancelled\(cause, signal\)/);
assert.match(disk, /cause instanceof Error && cause.name === "AbortError"/);
assert.match(disk, /setResultReadFailure\(null\)/);
assert.match(disk, /uiText\.diskUsage\.retainedResultStale/);
assert.match(disk, /onClick=\{\(\) => failure\(\)\.retry\(\)\}/);
assert.doesNotMatch(disk, /catch\([^)]*\) =>[^;]*setVolumes\(\[\]\)/);
const page = readFileSync(new URL("../src/features/diskUsage/DiskUsagePage.tsx", import.meta.url), "utf8");
assert.match(page, /props\.volumesReadState === "error"/);
assert.match(page, /props\.volumesReadState === "ready"/);
assert.match(page, /uiText\.diskUsage\.volumesReadFailed/);
assert.match(page, /uiText\.diskUsage\.retainedVolumesStale/);
assert.match(page, /onClick=\{props\.onRetryVolumes\}/);

assert.match(disk, /if \(!current \|\| resultReadFailure\(\) \|\| queryBusy\(\)/);
assert.match(disk, /requestedLayoutRoot !== null && requestedLayoutRoot !== current\.rootNodeId/);
assert.match(disk, /failure\?\.kind === "result" \? failure : null/);

assert.match(disk, /createSignal<DiskUsageScanSummary \| null \| undefined>\(undefined\)/);
assert.match(disk, /summary\(\) === undefined \|\| layout\(\) === undefined \? "loading" : "ready"/);
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

assert.match(workspace, /const owner = runtime\.diskUsage/);
assert.doesNotMatch(workspace, /createSignal<DiskUsage(?:Layout|Volume|ScanSummary)|getDiskUsage(?:Tree|Summary|Volumes)\(/);
assert.match(workspace, /createEffect\(on\(frontendWork\.frontendVisible, visible => owner\.setActive\(visible\)\)\)/);
