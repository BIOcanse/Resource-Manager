export type SettingsPatch = Record<string, unknown>;

export function createSettingsPatch(
  baseline: Record<string, unknown>,
  draft: Record<string, unknown>
): SettingsPatch {
  const patch = diffObject(baseline, draft);
  delete patch.version;
  return patch;
}

export function applySettingsPatch<T extends object>(settings: T, patch: SettingsPatch): T {
  const result = structuredClone(settings);
  const pending = [{ target: result as Record<string, unknown>, patch }];
  while (pending.length > 0) {
    const current = pending.pop()!;
    for (const [key, value] of Object.entries(current.patch)) {
      const targetValue = current.target[key];
      if (isPlainObject(value) && isPlainObject(targetValue)) {
        pending.push({ target: targetValue, patch: value });
      } else {
        current.target[key] = structuredClone(value);
      }
    }
  }
  return result;
}

function diffObject(
  baseline: Record<string, unknown>,
  draft: Record<string, unknown>
): SettingsPatch {
  const patch: SettingsPatch = {};
  for (const [key, draftValue] of Object.entries(draft)) {
    const baselineValue = baseline[key];
    if (isPlainObject(baselineValue) && isPlainObject(draftValue)) {
      const nested = diffObject(baselineValue, draftValue);
      if (Object.keys(nested).length > 0) {
        patch[key] = nested;
      }
      continue;
    }

    if (!areJsonValuesEqual(baselineValue, draftValue)) {
      patch[key] = structuredClone(draftValue);
    }
  }

  return patch;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function areJsonValuesEqual(left: unknown, right: unknown) {
  return JSON.stringify(left) === JSON.stringify(right);
}
