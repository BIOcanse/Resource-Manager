import type { ResourceTableRow, ResourceTableValue } from "../../types.ts";

/** Per column, the largest reading among the rows the table shows; heat is each cell's share of it. */
export type ResourceTableHeatMaxima = ReadonlyMap<string, number>;

export function resourceTableHeatMaxima(rows: readonly ResourceTableRow[]): ResourceTableHeatMaxima {
  const maxima = new Map<string, number>();
  for (const row of rows) {
    for (const columnId in row.values) {
      const value = row.values[columnId].value;
      if (value != null && value > (maxima.get(columnId) ?? 0)) {
        maxima.set(columnId, value);
      }
    }
  }
  return maxima;
}

export interface ResourceTableCellHeat {
  /** The whole reading as a percentage of the column maximum. */
  readonly heat: number;
  /** The reading without its shared part (sharedValue), as a percentage of the same maximum. */
  readonly privateHeat: number;
}

/** A cell's heat against its column maximum (0 without a reading or without a positive maximum). */
export function resourceTableCellHeat(cell: ResourceTableValue | null | undefined, maximum: number | undefined): ResourceTableCellHeat {
  const value = cell?.value;
  if (value == null || maximum === undefined || !(maximum > 0)) return { heat: 0, privateHeat: 0 };
  // Divide before multiplying so Number.MAX_VALUE readings do not overflow.
  return {
    heat: Math.max(0, value / maximum * 100),
    privateHeat: Math.max(0, (value - (cell?.sharedValue ?? 0)) / maximum * 100)
  };
}
