import type { ResourceTableRow } from "../../types.ts";

export function projectResourceTableHeat(rows: ResourceTableRow[]): ResourceTableRow[] {
  const maxima = new Map<string, number>();
  for (const row of rows) {
    for (const [columnId, cell] of Object.entries(row.values)) {
      if (cell.value != null && cell.value > (maxima.get(columnId) ?? 0)) {
        maxima.set(columnId, cell.value);
      }
    }
  }

  return rows.map((row) => ({
    ...row,
    values: Object.fromEntries(Object.entries(row.values).map(([columnId, cell]) => {
      const maximum = maxima.get(columnId) ?? 0;
      return [columnId, {
        ...cell,
        privateHeatPercent: maximum > 0 && cell.value != null
          ? Math.max(0, (cell.value - (cell.sharedValue ?? 0)) / maximum * 100)
          : 0,
        heatPercent: maximum > 0 && cell.value != null
          ? Math.max(0, cell.value / maximum * 100)
          : 0
      }];
    }))
  }));
}
