// Queries provide original and equivalent optimized algorithms.
// Execution mode selects a path; comparison mode evaluates both and reports differences.
// Query-specific algorithms live in optimizer.ts.

/** Original, optimized, or compare execution; compare returns the optimized result. */
export type ExecMode = "reference" | "optimized" | "compare";
export const EXEC_MODES: readonly ExecMode[] = ["reference", "optimized", "compare"];

/** A query comparison difference. */
export interface Mismatch {
  /** Query name. */
  query: string;
  /** Difference details and both results. */
  what: string;
  /** Conditions required for equivalence. */
  same: string;
}

/** Execution mode and an optional comparison-failure callback. */
export interface Execution {
  mode: ExecMode;
  onMismatch?: (m: Mismatch & { component: string }) => void;
}

/** Default execution when none is supplied. */
export const REFERENCE: Execution = { mode: "reference" };

/** Query algorithms and comparison. Deferred results may compare on every read; return the optimized result. */
export interface Query<A extends unknown[], R> {
  name: string;
  /** Equivalence conditions. */
  same: string;
  reference(...args: A): R;
  optimized(...args: A): R;
  compare(ref: R, opt: R, differ: (what: string) => void): R;
}

/** Select the query path; compare mode reports mismatches. */
export function pick<A extends unknown[], R>(q: Query<A, R>, mode: ExecMode, report: (m: Mismatch) => void): (...args: A) => R {
  if (mode === "reference") return (...a) => q.reference(...a);
  if (mode === "optimized") return (...a) => q.optimized(...a);
  const differ = (what: string) => report({ query: q.name, what, same: q.same });
  return (...a) => q.compare(q.reference(...a), q.optimized(...a), differ);
}

/** Value-result comparison: diff returns null on equality; return the optimized value. */
export const byValue = <R>(diff: (ref: R, opt: R) => string | null) => (ref: R, opt: R, differ: (what: string) => void): R => {
  const d = diff(ref, opt);
  if (d !== null) differ(d);
  return opt;
};
