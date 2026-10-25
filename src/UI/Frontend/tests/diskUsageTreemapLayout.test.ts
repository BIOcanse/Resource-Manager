import assert from "node:assert/strict";
import { decodeDiskUsageTree } from "../src/features/diskUsage/diskUsageTreeFacts.ts";
import { fullDiskUsageView, layoutDiskUsageTree, maximumDiskUsageTiles } from "../src/features/diskUsage/diskUsageTreemapLayout.ts";
import { tileFactsOf } from "../src/features/diskUsage/diskUsageLayoutFacts.ts";
import { diskTreeBuffer } from "./diskUsageTreeFixture.ts";

const nodes = [
  {id:10,parent:-1,size:10000,name:"root",directory:true,files:5},
  {id:12,parent:10,size:6000,name:"目录",directory:true,files:2},
  {id:14,parent:10,size:3000,name:"b.bin",directory:false},
  {id:16,parent:10,size:999,name:"c.bin",directory:false},
  {id:18,parent:10,size:1,name:"tiny",directory:false},
  {id:20,parent:12,size:4000,name:"a.bin",directory:false},
  {id:22,parent:12,size:2000,name:"d.bin",directory:false}
];
const tree = decodeDiskUsageTree(diskTreeBuffer(nodes, "D:\\数据"))!;
const layout = layoutDiskUsageTree(tree);
const depthOne = [...layout.nodeIds.keys()].filter(index => layout.depths[index] === 1);
let area = 0;
for (const index of depthOne) {
  const tileArea = layout.width[index] * layout.height[index];
  area += tileArea;
  assert.ok(Math.abs(tileArea - layout.sizes[index] / 10000) < 1e-6);
  assert.ok(layout.x[index] >= -1e-6 && layout.x[index] + layout.width[index] <= 1.000001);
  assert.ok(layout.y[index] >= -1e-6 && layout.y[index] + layout.height[index] <= 1.000001);
  for (const other of depthOne.filter(other => other > index)) {
    const overlapX = Math.min(layout.x[index] + layout.width[index], layout.x[other] + layout.width[other])
      - Math.max(layout.x[index], layout.x[other]);
    const overlapY = Math.min(layout.y[index] + layout.height[index], layout.y[other] + layout.height[other])
      - Math.max(layout.y[index], layout.y[other]);
    assert.ok(overlapX < 1e-6 || overlapY < 1e-6);
  }
}
assert.ok(Math.abs(area - 1) < 1e-6);
assert.equal(tileFactsOf(layout, 20)?.fullPath, "D:\\数据\\目录\\a.bin");
const drilled = layoutDiskUsageTree(tree, 12);
assert.equal(drilled.rootPath, "D:\\数据\\目录");
assert.equal(drilled.rootSizeBytes, 6000);
assert.equal(tileFactsOf(drilled, 20)?.fullPath, "D:\\数据\\目录\\a.bin");

const tiny = {...fullDiskUsageView,pixelWidth:64,pixelHeight:64};
assert.ok(layoutDiskUsageTree(tree,10,tiny).omittedCount > 0);
const far = layoutDiskUsageTree(tree,10,tiny), near = layoutDiskUsageTree(tree,10,{...tiny,scale:40});
assert.ok(near.nodeIds.length > far.nodeIds.length);
const corner = {...fullDiskUsageView,minX:0,minY:0,maxX:0.2,maxY:0.2};
const pruned = layoutDiskUsageTree(tree,10,corner);
for (let index = 1; index < pruned.nodeIds.length; index++) {
  assert.ok(pruned.x[index] <= .2 && pruned.y[index] <= .2);
}
assert.throws(() => layoutDiskUsageTree(tree, -7), /unavailable/);
for (const length of [0, 63, 70, diskTreeBuffer(nodes).byteLength - 1]) {
  assert.throws(() => decodeDiskUsageTree(diskTreeBuffer(nodes).slice(0,length)));
}
for (const version of [1,2]) {
  const old = diskTreeBuffer(nodes); new DataView(old).setUint32(4,version,true);
  assert.throws(() => decodeDiskUsageTree(old), /protocol/);
}
const cycle = diskTreeBuffer([{...nodes[0]}, {...nodes[1],parent:12}]);
assert.throws(() => decodeDiskUsageTree(cycle), /parent/);

// The safety limit remains a frontend limit; every scanned node is still present in facts.
const wideNodes = Array.from({length:maximumDiskUsageTiles + 2}, (_,index) => ({
  id:index,parent:index===0 ? -1 : 0,size:index===0 ? maximumDiskUsageTiles+1 : 1,
  name:"",directory:index===0
}));
const wideTree = decodeDiskUsageTree(diskTreeBuffer(wideNodes))!;
const bounded = layoutDiskUsageTree(wideTree,0,{...fullDiskUsageView,pixelWidth:32768,pixelHeight:32768,scale:64});
assert.equal(wideTree.nodeIds.length,maximumDiskUsageTiles+2);
assert.equal(bounded.nodeIds.length,maximumDiskUsageTiles);
assert.equal(bounded.omittedCount,2);
console.log("diskUsageTreemapLayout: facts, area, pruning, paths, protocol validation and 400000-tile bound passed");

// 键盘打开右键菜单的位置：选中方格可见部分的中心，和 hitTest 用同一套视口换算。
{
  const { hitTest, visibleNodeCentre } = await import("../src/features/diskUsage/diskUsageTreemapPaint.ts");
  const width = 800;
  const height = 400;
  for (const viewport of [{ scale: 1, offsetX: 0, offsetY: 0 }, { scale: 3, offsetX: 0.2, offsetY: 0.1 }]) {
    for (const index of depthOne) {
      const nodeId = layout.nodeIds[index];
      const centre = visibleNodeCentre(layout, viewport, width, height, nodeId)!;
      assert.ok(centre.x >= 0 && centre.x <= width && centre.y >= 0 && centre.y <= height);
      const left = (layout.x[index] - viewport.offsetX) * width * viewport.scale;
      const right = left + layout.width[index] * width * viewport.scale;
      const top = (layout.y[index] - viewport.offsetY) * height * viewport.scale;
      const bottom = top + layout.height[index] * height * viewport.scale;
      const visible = right > 0 && left < width && bottom > 0 && top < height;
      if (visible) {
        // 中心落在这个方格（或画在它上面的子方格）里：命中检测找到的是它自己或它的后代。
        let hit = hitTest(layout, viewport, width, height, centre.x, centre.y);
        let guard = 0;
        while (hit >= 0 && hit !== nodeId && guard++ < 64) {
          hit = layout.parentIds[layout.indexByNodeId.get(hit)!];
        }
        assert.equal(hit, nodeId, `centre of node ${nodeId} hits it`);
      }
    }
  }
  assert.equal(visibleNodeCentre(layout, { scale: 1, offsetX: 0, offsetY: 0 }, width, height, -12345), null);
  // 整个在视口外的方格落到画布边上。
  const offscreen = visibleNodeCentre(layout, { scale: 4, offsetX: 0.75, offsetY: 0.75 }, width, height, layout.nodeIds[depthOne[0]]);
  assert.ok(offscreen && offscreen.x >= 0 && offscreen.x <= width && offscreen.y >= 0 && offscreen.y <= height);
}
