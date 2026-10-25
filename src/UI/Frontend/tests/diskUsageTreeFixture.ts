export interface DiskTreeNodeFixture {
  id: number; parent: number; size: number; name: string; directory: boolean; files?: number;
  childTotal?: number; firstChild?: number; nextSibling?: number;
}
export function diskTreeBuffer(nodes: readonly DiskTreeNodeFixture[], rootPath = "C:\\", nextCursor = -1): ArrayBuffer {
  const encoder = new TextEncoder(), names = nodes.map(node => encoder.encode(node.name)), path = encoder.encode(rootPath);
  const count = nodes.length, columnsEnd = 64 + 37 * count, lengthsStart = columnsEnd + (4 - columnsEnd % 4) % 4;
  const buffer = new ArrayBuffer(lengthsStart + 4 * count + names.reduce((sum, name) => sum + name.length, 0) + path.length);
  const header = new DataView(buffer);
  header.setUint32(0, 0x55444d52, true); header.setUint32(4, 3, true); header.setUint32(8, count, true);
  header.setInt32(12, nodes[0]?.id ?? -1, true); header.setFloat64(24, nodes[0]?.size ?? 0, true);
  header.setInt32(16, nextCursor, true);
  header.setUint32(60, path.length, true);
  let offset = 64;
  new Float64Array(buffer, offset, count).set(nodes.map(node => node.size)); offset += 8 * count;
  const childTotals = new Map<number, number>(), first = new Map<number, number>(), last = new Map<number, number>(), next = new Map<number, number>();
  for (const node of nodes.slice(1)) {
    childTotals.set(node.parent, (childTotals.get(node.parent) ?? 0) + node.size);
    if (!first.has(node.parent)) first.set(node.parent, node.id);
    const previous = last.get(node.parent);
    if (previous !== undefined) next.set(previous, node.id);
    last.set(node.parent, node.id);
  }
  new Float64Array(buffer, offset, count).set(nodes.map(node => node.childTotal ?? childTotals.get(node.id) ?? 0)); offset += 8 * count;
  new Int32Array(buffer, offset, count).set(nodes.map(node => node.id)); offset += 4 * count;
  new Int32Array(buffer, offset, count).set(nodes.map(node => node.parent)); offset += 4 * count;
  new Int32Array(buffer, offset, count).set(nodes.map(node => node.files ?? 0)); offset += 4 * count;
  new Int32Array(buffer, offset, count).set(nodes.map(node => node.firstChild ?? first.get(node.id) ?? -1)); offset += 4 * count;
  new Int32Array(buffer, offset, count).set(nodes.map(node => node.nextSibling ?? next.get(node.id) ?? -1)); offset += 4 * count;
  new Uint8Array(buffer, offset, count).set(nodes.map(node => Number(node.directory)));
  new Uint32Array(buffer, lengthsStart, count).set(names.map(name => name.length)); offset = lengthsStart + 4 * count;
  for (const name of names) { new Uint8Array(buffer, offset, name.length).set(name); offset += name.length; }
  new Uint8Array(buffer, offset, path.length).set(path);
  return buffer;
}
