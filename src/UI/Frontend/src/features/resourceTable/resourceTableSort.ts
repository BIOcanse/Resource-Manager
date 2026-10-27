/** 资源表的排序：按哪一列、哪个方向。impact 不是一列，是默认的综合排序（各列占比按权重相加，resourceFacts.ts）。 */
export interface ResourceTableSort {
  readonly columnId: string;
  readonly direction: "asc" | "desc";
}

/** 默认的综合排序：没有任何一列显示排序箭头。 */
export const defaultResourceTableSort: ResourceTableSort = Object.freeze({ columnId: "impact", direction: "desc" });

/**
 * 点一次表头（鼠标或键盘）后的排序。用户 10-27：「点一次从高到低，点两次从低到高，再点一次取消排序回到默认的综合排序」。
 * 换一列从降序开始；同一列降序之后升序；升序之后回到综合排序。每一列都这样，文字列（名称、状态）的降序是排序规则下的倒序。
 */
export function nextResourceTableSort(current: ResourceTableSort, columnId: string): ResourceTableSort {
  if (current.columnId !== columnId) return { columnId, direction: "desc" };
  return current.direction === "desc" ? { columnId, direction: "asc" } : defaultResourceTableSort;
}
