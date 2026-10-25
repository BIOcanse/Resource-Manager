import assert from "node:assert/strict";
import { resourceTableColumnsEqual } from
  "../src/features/resourceTable/resourceTableColumnIdentity.ts";
import type { ResourceTableColumn } from "../src/types.ts";

const cpuColumn: ResourceTableColumn = {
  id: "cpu",
  unit: "%",
  visible: true,
  sortable: true,
  width: 120,
  source: null
};
const memoryColumn: ResourceTableColumn = {
  id: "memory",
  unit: "B",
  visible: true,
  sortable: true,
  width: 140,
  source: null
};

assert.equal(resourceTableColumnsEqual(
  [cpuColumn, memoryColumn],
  [{ ...cpuColumn }, { ...memoryColumn }]
), true, "equivalent snapshot columns preserve the existing render identity");

for (const [field, value] of [
  ["id", "cpu-next"],
  ["unit", "ms"],
  ["visible", false],
  ["sortable", false],
  ["width", 121],
  // 换了来源，表头措辞跟着变。
  ["source", "process_all_private_commit_actual"]
] as const) {
  assert.equal(resourceTableColumnsEqual(
    [cpuColumn],
    [{ ...cpuColumn, [field]: value }]
  ), false, `column ${field} changes invalidate render identity`);
}

assert.equal(resourceTableColumnsEqual(
  [cpuColumn, memoryColumn],
  [memoryColumn, cpuColumn]
), false, "column order changes invalidate render identity");
assert.equal(resourceTableColumnsEqual([cpuColumn], [cpuColumn, memoryColumn]), false);
