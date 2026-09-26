import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  applyByteUnitMode,
  currentByteUnitMode,
  formatBytePair,
  formatBytes,
  formatBitsPerSecond,
  normalizeByteUnitMode,
  usesBinaryUnits
} from "../src/presentation/byteUnits.ts";

const gib = 1024 ** 3;
const gb = 1000 ** 3;

// 认不出的模式一律落回 native。
assert.equal(normalizeByteUnitMode(undefined), "native");
assert.equal(normalizeByteUnitMode("GiB"), "native");
assert.equal(normalizeByteUnitMode("binary"), "binary");
assert.equal(normalizeByteUnitMode("decimal"), "decimal");

// 模式 3：内存类是二进制天性，存储类是十进制天性。
assert.equal(usesBinaryUnits("memory", "native"), true);
assert.equal(usesBinaryUnits("storage", "native"), false);
// 另外两种模式一刀切，和类别无关。
for (const kind of ["memory", "storage"] as const) {
  assert.equal(usesBinaryUnits(kind, "binary"), true);
  assert.equal(usesBinaryUnits(kind, "decimal"), false);
}

// 标签必须和实际用的进制一致 —— 这条是整件事的起因，不许再出现「写 GB 算 1024」。
assert.equal(formatBytes(32 * gib, "memory", "native"), "32.0 GiB");
assert.equal(formatBytes(32 * gib, "memory", "decimal"), "34.4 GB");
assert.equal(formatBytes(gb, "storage", "native"), "1.00 GB");
// 10^9 字节按 1024 换算不到 1 GiB，所以停在 MiB 档 —— 跳档是按实际进制走的。
assert.equal(formatBytes(gb, "storage", "binary"), "954 MiB");

// 1 TB 的盘在「按物理性质」下要对得上盘上印的标称容量。
assert.equal(formatBytes(1000 ** 4, "storage", "native"), "1.00 TB");

// 已用 / 总量必须落在同一档，单位只写一次。
assert.equal(
  formatBytePair(22.8 * gib, 31.2 * gib, "memory", "native"),
  "22.8 / 31.2 GiB");
assert.equal(formatBytePair(0, 0, "memory", "native"), "0 B");

// 空读数不编造数字。
assert.equal(formatBytes(null, "memory", "native"), "--");
assert.equal(formatBytes(Number.NaN, "memory", "native"), "--");

// 链路速率天生按位计，十进制，不随容量模式变。
assert.equal(formatBitsPerSecond(10 * 1000 ** 3), "10.0 Gbit/s");
applyByteUnitMode("binary");
assert.equal(currentByteUnitMode(), "binary");
assert.equal(formatBitsPerSecond(10 * 1000 ** 3), "10.0 Gbit/s");
// 省略模式的调用读当前模式，所以切换后所有数字一起变。
assert.equal(formatBytes(gb, "storage"), "954 MiB");
applyByteUnitMode("native");
assert.equal(formatBytes(gb, "storage"), "1.00 GB");

// 换算只有一个所有者：别处不许再出现按 1024 手算的字节格式化。
const offenders = [
  "../src/utils.ts",
  "../src/features/deviceTopology/adapters/adapterEvidence.ts",
  "../src/data/operations/uint64Decimal.ts",
  "../src/features/details/CpuTopologyDiagram.tsx",
  "../src/components/SoftwareDetailModal.tsx",
  "../src/features/details/HostManagerSmartCoordinatorDetailsReport.tsx"
];
for (const relativePath of offenders) {
  const source = readFileSync(new URL(relativePath, import.meta.url), "utf8");
  assert.doesNotMatch(
    source,
    /[/>]=?\s*1024(?!\s*[，,、)]?\s*(?:再|还原))/,
    `${relativePath} 不应再自己按 1024 换算，改用 presentation/byteUnits.ts`);
}

console.log("byteUnitsContract: ok");
