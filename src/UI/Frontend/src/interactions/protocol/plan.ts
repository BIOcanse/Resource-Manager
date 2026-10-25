// 构造、优化、发行（协议第 4.7 节）：运行时向构造要的东西叫查询，每个查询有原始和优化两个算法。
// 原始算法按构造现算，是结果的定义；优化算法结果相同、更快，写明相同的条件。执行方式定下用哪一个；对照时两个都算，比结果。
// 这里只有通用的部分；有哪些查询、各自的算法在 optimizer.ts。

/** 执行方式：原始（开发时设计、调试用），优化（发行用），对照（两个都算，用优化的，读到不同就报）。 */
export type ExecMode = "reference" | "optimized" | "compare";
export const EXEC_MODES: readonly ExecMode[] = ["reference", "optimized", "compare"];

/** 对照读到的一处不同。 */
export interface Mismatch {
  /** 哪个查询。 */
  query: string;
  /** 不同在哪、两边各是什么。 */
  what: string;
  /** 这个查询结果相同的条件，照写，看是不是越过了条件。 */
  same: string;
}

/** 运行时的执行：方式，和对照读到不同时除了写日志还告诉谁（测试收集、页面报错）。 */
export interface Execution {
  mode: ExecMode;
  onMismatch?: (m: Mismatch & { component: string }) => void;
}

/** 原始执行：运行时不传执行时用它。 */
export const REFERENCE: Execution = { mode: "reference" };

/**
 * 一个查询：原始、优化两个算法和对照的比法。compare 拿到两边的结果，返回运行时用的那一份（优化的）；
 * 结果是要接着读的东西（例如一段运动）时，比法包一层，每读一次比一次。
 */
export interface Query<A extends unknown[], R> {
  name: string;
  /** 结果相同的条件。 */
  same: string;
  reference(...args: A): R;
  optimized(...args: A): R;
  compare(ref: R, opt: R, differ: (what: string) => void): R;
}

/** 按执行方式取一个查询的算法。对照时两个都算，读到不同交给 report。 */
export function pick<A extends unknown[], R>(q: Query<A, R>, mode: ExecMode, report: (m: Mismatch) => void): (...args: A) => R {
  if (mode === "reference") return (...a) => q.reference(...a);
  if (mode === "optimized") return (...a) => q.optimized(...a);
  const differ = (what: string) => report({ query: q.name, what, same: q.same });
  return (...a) => q.compare(q.reference(...a), q.optimized(...a), differ);
}

/** 结果是一个值的查询的比法：diff 写出哪里不同，相同返回 null；用优化的那一份。 */
export const byValue = <R>(diff: (ref: R, opt: R) => string | null) => (ref: R, opt: R, differ: (what: string) => void): R => {
  const d = diff(ref, opt);
  if (d !== null) differ(d);
  return opt;
};
