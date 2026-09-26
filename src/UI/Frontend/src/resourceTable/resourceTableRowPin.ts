import type { ResourceTableRow } from "../types";

/**
 * 右键某一行时把它钉在当前位置：新快照到来后这一行仍然停在原来的第几行，
 * 数值照常更新，其余行照常按后端的顺序排。菜单关掉就不钉了。
 *
 * 钉住的行如果在新快照里消失了，就什么都不做——不为一个已经不存在的行占位。
 */
export function pinResourceTableRow(
  rows: readonly ResourceTableRow[],
  pinnedRowId: string | null,
  pinnedIndex: number | null
): readonly ResourceTableRow[] {
  if (!pinnedRowId || pinnedIndex === null || pinnedIndex < 0) {
    return rows;
  }

  const currentIndex = rows.findIndex((row) => row.id === pinnedRowId);
  if (currentIndex < 0) {
    return rows;
  }

  const target = Math.min(pinnedIndex, rows.length - 1);
  if (currentIndex === target) {
    return rows;
  }

  const reordered = [...rows];
  const [pinned] = reordered.splice(currentIndex, 1);
  reordered.splice(target, 0, pinned);
  return reordered;
}
