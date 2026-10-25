/** A bounded RMDU v3 hierarchy page. All rectangles and pruning are frontend values. */
export interface DiskUsageTreeFacts {
  rootNodeId: number;
  rootPath: string;
  rootSizeBytes: number;
  nextCursorNodeId: number;
  sizes: Float64Array;
  childSizeTotals: Float64Array;
  nodeIds: Int32Array;
  parentIds: Int32Array;
  fileCounts: Int32Array;
  firstChildIds: Int32Array;
  nextSiblingIds: Int32Array;
  directoryFlags: Uint8Array;
  names: string[];
  indexByNodeId: Map<number, number>;
  childOffsets: Uint32Array;
  children: Uint32Array;
  subtreeNodeCounts: Uint32Array;
  subtreeComplete: Uint8Array;
}

export const diskUsageTreeMagic = 0x5544_4d52;
export const diskUsageTreeVersion = 3;
export const diskUsageTreeHeaderBytes = 64;

export function decodeDiskUsageTree(buffer: ArrayBuffer): DiskUsageTreeFacts | null {
  if (buffer.byteLength < diskUsageTreeHeaderBytes) throw new Error("Truncated disk tree header");
  const header = new DataView(buffer);
  if (header.getUint32(0, true) !== diskUsageTreeMagic
      || header.getUint32(4, true) !== diskUsageTreeVersion) throw new Error("Unsupported disk tree protocol");
  const count = header.getUint32(8, true);
  const rootNodeId = header.getInt32(12, true);
  const nextCursorNodeId = header.getInt32(16, true);
  const pathBytes = header.getUint32(60, true);
  if (count === 0) {
    if (rootNodeId !== -1 || nextCursorNodeId !== -1 || buffer.byteLength !== diskUsageTreeHeaderBytes) throw new Error("Invalid empty disk tree");
    return null;
  }
  let offset = diskUsageTreeHeaderBytes;
  const columnsEnd = offset + 37 * count;
  const lengthsStart = columnsEnd + (4 - columnsEnd % 4) % 4;
  if (lengthsStart + 4 * count + pathBytes > buffer.byteLength) throw new Error("Truncated disk tree columns");
  const sizes = new Float64Array(buffer, offset, count); offset += 8 * count;
  const childSizeTotals = new Float64Array(buffer, offset, count); offset += 8 * count;
  const nodeIds = new Int32Array(buffer, offset, count); offset += 4 * count;
  const parentIds = new Int32Array(buffer, offset, count); offset += 4 * count;
  const fileCounts = new Int32Array(buffer, offset, count); offset += 4 * count;
  const firstChildIds = new Int32Array(buffer, offset, count); offset += 4 * count;
  const nextSiblingIds = new Int32Array(buffer, offset, count); offset += 4 * count;
  const directoryFlags = new Uint8Array(buffer, offset, count);
  const nameLengths = new Uint32Array(buffer, lengthsStart, count);
  offset = lengthsStart + 4 * count;
  const names = new Array<string>(count);
  const indexByNodeId = new Map<number, number>();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  for (let index = 0; index < count; index++) {
    const end = offset + nameLengths[index];
    if (end > buffer.byteLength - pathBytes) throw new Error("Truncated disk tree names");
    if (nodeIds[index] < 0 || indexByNodeId.has(nodeIds[index])
        || !Number.isSafeInteger(sizes[index]) || sizes[index] < 0
        || !Number.isSafeInteger(childSizeTotals[index]) || childSizeTotals[index] < 0
        || firstChildIds[index] < -1 || nextSiblingIds[index] < -1
        || fileCounts[index] < 0 || directoryFlags[index] > 1) throw new Error("Invalid disk tree fact");
    names[index] = decoder.decode(new Uint8Array(buffer, offset, nameLengths[index]));
    indexByNodeId.set(nodeIds[index], index);
    offset = end;
  }
  if (offset + pathBytes !== buffer.byteLength || nodeIds[0] !== rootNodeId) throw new Error("Invalid disk tree root or length");
  const childOffsets = new Uint32Array(count + 1);
  for (let index = 1; index < count; index++) {
    const parent = indexByNodeId.get(parentIds[index]);
    // The scan contract orders parents before children. This also excludes cycles.
    if (parent === undefined || parent >= index || directoryFlags[parent] !== 1) throw new Error("Invalid disk tree parent");
    childOffsets[parent + 1]++;
  }
  for (let index = 1; index <= count; index++) childOffsets[index] += childOffsets[index - 1];
  const children = new Uint32Array(count - 1);
  const cursor = childOffsets.slice();
  for (let index = 1; index < count; index++) children[cursor[indexByNodeId.get(parentIds[index])!]++] = index;
  for (let index = 0; index < count; index++) {
    children.subarray(childOffsets[index], childOffsets[index + 1])
      .sort((a, b) => sizes[b] - sizes[a] || nodeIds[a] - nodeIds[b]);
  }
  const subtreeNodeCounts = new Uint32Array(count).fill(1);
  const subtreeComplete = new Uint8Array(count).fill(1);
  for (let index = 0; index < count; index++) {
    if (firstChildIds[index] !== -1 && !indexByNodeId.has(firstChildIds[index])) subtreeComplete[index] = 0;
    if (index > 0 && nextSiblingIds[index] !== -1 && !indexByNodeId.has(nextSiblingIds[index])) {
      subtreeComplete[indexByNodeId.get(parentIds[index])!] = 0;
    }
  }
  // This index is built once per page. Tiny drilled subtrees never allocate scan-sized work arrays.
  for (let index = count - 1; index > 0; index--) {
    const parent = indexByNodeId.get(parentIds[index])!;
    subtreeNodeCounts[parent] += subtreeNodeCounts[index];
    subtreeComplete[parent] &= subtreeComplete[index];
  }
  return {
    rootNodeId, rootPath: decoder.decode(new Uint8Array(buffer, offset, pathBytes)),
    rootSizeBytes: header.getFloat64(24, true), nextCursorNodeId, sizes, childSizeTotals,
    nodeIds, parentIds, fileCounts, firstChildIds, nextSiblingIds,
    directoryFlags, names, indexByNodeId, childOffsets, children, subtreeNodeCounts, subtreeComplete
  };
}
