/** Interaction budget only. The stable 1 Hz sampling rule is independent.
 * Entries require CPU-profile evidence of deliberate effect work, never layout waste.
 * effect must name the actual Lodestone/motion component; step is an exact journey label.
 * measuredOn: { date: "YYYY-MM-DD", jobId: "queue job id" }.
 */
export const INTERACTION_LONG_TASK_LIMIT_MS = 50;
export const EFFECT_OVERRUN_TOLERANCE_MS = 5;

export interface EffectOverrun { effect: string; step: string; measuredMs: number;
  measuredOn: { date: string; jobId: string }; reason: string }
export interface InteractionLongTask { start: number; duration: number }
export interface InteractionWindow { start: number; end: number; step: string }
export const ALLOWED_EFFECT_OVERRUNS: readonly EffectOverrun[] = Object.freeze([]);

export function assessInteractionLongTasks<T extends InteractionLongTask>(tasks: readonly T[], windows: readonly InteractionWindow[], registry: readonly EffectOverrun[] = ALLOWED_EFFECT_OVERRUNS) {
  const steps = new Set();
  for (const entry of registry) {
    if (!entry.effect?.trim() || !entry.step?.trim() || !entry.reason?.trim()
      || !Number.isFinite(entry.measuredMs) || entry.measuredMs <= INTERACTION_LONG_TASK_LIMIT_MS
      || !/^\d{4}-\d{2}-\d{2}$/.test(entry.measuredOn?.date) || !entry.measuredOn?.jobId?.trim()
      || steps.has(entry.step)) throw new Error(`Invalid effect overrun entry: ${JSON.stringify(entry)}`);
    steps.add(entry.step);
  }
  return tasks.filter((task) => task.duration > INTERACTION_LONG_TASK_LIMIT_MS).map((task) => {
    // A click's dispatch task can start before its capture listener records t0.
    // Half-open interval overlap includes that task and excludes adjacent windows.
    const window = windows.find((item) => task.start < item.end && task.start + task.duration > item.start);
    const step = window?.step ?? "unattributed (outside switch windows)";
    const entry = window ? registry.find((item) => item.step === step) ?? null : null;
    const allowedMs = entry ? entry.measuredMs + EFFECT_OVERRUN_TOLERANCE_MS : INTERACTION_LONG_TASK_LIMIT_MS;
    const passed = Boolean(entry && task.duration <= allowedMs);
    return { ...task, step, entry, allowedMs, passed,
      message: `${step}: ${task.duration} ms; ${entry
        ? `entry=${JSON.stringify(entry)}; limit=${entry.measuredMs} + EFFECT_OVERRUN_TOLERANCE_MS (${EFFECT_OVERRUN_TOLERANCE_MS}) = ${allowedMs} ms`
        : `unregistered; limit=${INTERACTION_LONG_TASK_LIMIT_MS} ms`}` };
  });
}
