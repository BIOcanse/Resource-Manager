// Run with Invoke-WithMemoryLimit.ps1. Reads metadata only; never reads file contents.
import { readdirSync, lstatSync, readFileSync, writeFileSync } from "node:fs";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import { stripTypeScriptTypes } from "node:module";
import { gzipSync } from "node:zlib";
import type { DiskUsageLayout } from "../../src/features/diskUsage/diskUsageLayoutTypes.ts";
import type { DiskUsageViewWindow } from "../../src/features/diskUsage/diskUsageViewport.ts";

interface BenchmarkNode { parent: number; name: string; size: number; files: number; directory: boolean; path: string }
type WorkerRequest = { buffer: ArrayBuffer } | { buffer?: never; view?: DiskUsageViewWindow; rootNodeId?: number };
interface DecodeResult { decodeMs: number }
interface LayoutResult { layout: DiskUsageLayout; layoutMs: number; workingArrayBytes: number; largestWorkingArray: number }

const root = resolve(process.argv[2] ?? "C:/Windows/WinSxS");
const output = process.argv[3];
const maximumNodes = Number(process.argv[4] ?? 400000);
const nodes: BenchmarkNode[] = [{ parent: -1, name: root, size: 0, files: 0, directory: true, path: root }];
let skipped = 0, truncated = false;
const started = performance.now();
const flatFixture = process.argv.includes("--flat-fixture");
if (flatFixture) {
  nodes.push({parent:0,name:"tiny-dir",size:0,files:0,directory:true,path:""});
  nodes.push({parent:1,name:"tiny-file.bin",size:1,files:0,directory:false,path:""});
  for (let index = 3; index < maximumNodes; index++) nodes.push({parent:0,name:`file-${index}.bin`,size:1,files:0,directory:false,path:""});
}
for (let cursor = 0; !flatFixture && cursor < nodes.length; cursor++) {
  const node = nodes[cursor];
  if (!node.directory) continue;
  let entries: import("node:fs").Dirent[];
  try { entries = readdirSync(node.path, { withFileTypes: true }); }
  catch { skipped++; continue; }
  for (const entry of entries) {
    if (nodes.length >= maximumNodes) { truncated = true; break; }
    if (entry.isSymbolicLink()) { skipped++; continue; }
    const path = join(node.path, entry.name);
    try {
      const stat = lstatSync(path);
      if (!stat.isDirectory() && !stat.isFile()) { skipped++; continue; }
      nodes.push({ parent: cursor, name: entry.name, size: stat.isFile() ? stat.size : 0,
        files: 0, directory: stat.isDirectory(), path });
    } catch { skipped++; }
  }
  if (truncated) break;
}
for (let index = nodes.length - 1; index > 0; index--) {
  const node = nodes[index], parent = nodes[node.parent];
  parent.size += node.size; parent.files += node.directory ? node.files : 1;
}
const scanMs = performance.now() - started;
const count = nodes.length, encoded = nodes.map(node => Buffer.from(node.name)), rootPath = Buffer.from(root);
const columnsEnd = 64 + 37 * count, lengthsStart = columnsEnd + (4 - columnsEnd % 4) % 4;
const buffer = Buffer.alloc(lengthsStart + 4 * count + encoded.reduce((sum, name) => sum + name.length, 0) + rootPath.length);
buffer.writeUInt32LE(0x55444d52, 0); buffer.writeUInt32LE(3, 4); buffer.writeUInt32LE(count, 8);
buffer.writeInt32LE(0, 12); buffer.writeDoubleLE(nodes[0].size, 24); buffer.writeUInt32LE(rootPath.length, 60);
buffer.writeInt32LE(-1,16);
let offset = 64;
for (const node of nodes) { buffer.writeDoubleLE(node.size, offset); offset += 8; }
const childTotals = new Float64Array(count), firstChild = new Int32Array(count).fill(-1);
const nextSibling = new Int32Array(count).fill(-1), lastChild = new Int32Array(count).fill(-1);
for (let index = 1; index < count; index++) {
  const parent = nodes[index].parent;
  childTotals[parent] += nodes[index].size;
  if (firstChild[parent] === -1) firstChild[parent] = index;
  if (lastChild[parent] !== -1) nextSibling[lastChild[parent]] = index;
  lastChild[parent] = index;
}
for (const total of childTotals) { buffer.writeDoubleLE(total,offset); offset+=8; }
for (let index = 0; index < count; index++, offset += 4) buffer.writeInt32LE(index, offset);
for (const node of nodes) { buffer.writeInt32LE(node.parent, offset); offset += 4; }
for (const node of nodes) { buffer.writeInt32LE(node.files, offset); offset += 4; }
for (const child of firstChild) { buffer.writeInt32LE(child,offset); offset+=4; }
for (const sibling of nextSibling) { buffer.writeInt32LE(sibling,offset); offset+=4; }
for (const node of nodes) buffer[offset++] = Number(node.directory);
offset = lengthsStart;
for (const name of encoded) { buffer.writeUInt32LE(name.length, offset); offset += 4; }
for (const name of encoded) { name.copy(buffer, offset); offset += name.length; }
rootPath.copy(buffer, offset);

const modulePath = process.env.RM_PLAYWRIGHT_MODULE;
if (!modulePath) throw new Error("Set RM_PLAYWRIGHT_MODULE to the existing Playwright module");
const { chromium } = await import(pathToFileURL(modulePath).href) as typeof import("playwright-core");
const browser = await chromium.launch({ headless: true, executablePath: process.env.RM_PLAYWRIGHT_BROWSER
  ?? "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" });
try {
  const page = await browser.newPage();
  // Use exactly the production functions, with erased types; no benchmark reimplementation.
  const sources = ["diskUsageTreeFacts.ts", "diskUsageTreemapLayout.ts"].map(name =>
    stripTypeScriptTypes(readFileSync(new URL(`../../src/features/diskUsage/${name}`, import.meta.url), "utf8"))
      .replace(/^export /gm, ""));
  const tracker = `let allocatedBytes=0, largestArray=0;
    for(const key of ['Int32Array','Uint32Array','Uint8Array','Float32Array','Float64Array']){
      const Native=globalThis[key];globalThis[key]=new Proxy(Native,{construct(target,args){
        if(typeof args[0]==='number'){allocatedBytes+=args[0]*Native.BYTES_PER_ELEMENT;largestArray=Math.max(largestArray,args[0]);}
        return Reflect.construct(target,args);
      }});}`;
  const workerSource = tracker + sources.join("\n") + `\nlet tree;
    onmessage = ({data}) => { const start = performance.now();
      if (data.buffer) { tree = decodeDiskUsageTree(data.buffer); postMessage({decodeMs:performance.now()-start}); return; }
      allocatedBytes=0;largestArray=0;
      const layout = layoutDiskUsageTree(tree, data.rootNodeId ?? tree.rootNodeId, data.view);
      postMessage({layout,layoutMs:performance.now()-start,workingArrayBytes:allocatedBytes,largestWorkingArray:largestArray}, diskUsageLayoutTransfers(layout)); };`;
  const results = await page.evaluate(async ({ workerSource, bytes, flatFixture }) => {
    const worker = new Worker(URL.createObjectURL(new Blob([workerSource], { type: "text/javascript" })));
    const request = <T extends DecodeResult | LayoutResult>(data: WorkerRequest) => new Promise<T & { roundTripMs: number }>((resolve, reject) => {
      const start = performance.now();
      worker.onmessage = ({data}: MessageEvent<T>) => resolve({ ...data, roundTripMs: performance.now() - start });
      worker.onerror = event => reject(new Error(event.message));
      worker.postMessage(data, data.buffer ? [data.buffer] : []);
    });
    const buffer = Uint8Array.from(atob(bytes), c => c.charCodeAt(0)).buffer;
    const decode = await request<DecodeResult>({buffer});
    const layouts = [];
    for (const view of [
      {pixelWidth:800,pixelHeight:500,scale:1,minX:0,minY:0,maxX:1,maxY:1},
      {pixelWidth:1920,pixelHeight:1080,scale:1,minX:0,minY:0,maxX:1,maxY:1},
      {pixelWidth:3840,pixelHeight:2160,scale:1,minX:0,minY:0,maxX:1,maxY:1},
      {pixelWidth:1920,pixelHeight:1080,scale:16,minX:0.1,minY:0.1,maxX:0.1625,maxY:0.1625},
      {pixelWidth:32768,pixelHeight:32768,scale:64,minX:0,minY:0,maxX:1,maxY:1}
    ]) {
      const runs = [];
      for (let attempt = 0; attempt < 3; attempt++) {
        const {layout,layoutMs,roundTripMs,workingArrayBytes,largestWorkingArray} = await request<LayoutResult>({view});
        const namesBytes = layout.names.reduce((sum,name)=>sum+new TextEncoder().encode(name).length,0);
        const oldColumnsBytes = 41 * layout.nodeIds.length;
        runs.push({layoutMs,roundTripMs,workingArrayBytes,largestWorkingArray,tiles:layout.nodeIds.length,omitted:layout.omittedCount,
          previousLayoutWireBytes:64+oldColumnsBytes+(4-oldColumnsBytes%4)%4+4*layout.nodeIds.length+namesBytes+new TextEncoder().encode(layout.rootPath).length,
          typedArrayBytes: [layout.nodeIds,layout.parentIds,layout.depths,layout.x,layout.y,layout.width,
            layout.height,layout.directoryFlags,layout.sizes,layout.fileCounts,layout.subtreeComplete].reduce((sum,col)=>sum+col.byteLength,0)});
      }
      layouts.push({view,runs});
    }
    const tinyDrill = [];
    if (flatFixture) {
      for (let attempt=0;attempt<20;attempt++) {
        const {layout,layoutMs,roundTripMs,workingArrayBytes,largestWorkingArray}=await request<LayoutResult>({rootNodeId:1});
        tinyDrill.push({tiles:layout.nodeIds.length,layoutMs,roundTripMs,workingArrayBytes,largestWorkingArray});
      }
    }
    worker.terminate();
    return {decode,layouts,tinyDrill};
  }, {workerSource,bytes:buffer.toString("base64"),flatFixture});
  const report = {root,flatFixture,count,skipped,truncated,scanMs,wireBytes:buffer.length,gzipBytes:gzipSync(buffer).length,
    browser:browser.version(),results};
  if (output) writeFileSync(output, JSON.stringify(report,null,2));
  console.log(JSON.stringify(report,null,2));
} finally { await browser.close(); }
