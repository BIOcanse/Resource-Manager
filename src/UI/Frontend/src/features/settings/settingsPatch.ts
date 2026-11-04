export type SettingsPatch = Record<string, unknown>;

/** Replace whole sections when key deletion matters; merging overrides cannot represent removal. */
export const wholeValueSettingsKeys: ReadonlySet<string> = new Set(["memoryCpuTier"]);

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
    const atRoot = current.patch === patch;
    for (const [key, value] of Object.entries(current.patch)) {
      const targetValue = current.target[key];
      if (isPlainObject(value) && isPlainObject(targetValue) && !(atRoot && wholeValueSettingsKeys.has(key))) {
        pending.push({ target: targetValue, patch: value });
      } else {
        current.target[key] = structuredClone(value);
      }
    }
  }
  return result;
}

// Use an explicit stack to build nested object patches.
// Prune empty children in reverse creation order so descendants are checked before parents.
function diffObject(
  baseline: Record<string, unknown>,
  draft: Record<string, unknown>
): SettingsPatch {
  const root: SettingsPatch = {};
  const nestedPatches: { parent: SettingsPatch; key: string; patch: SettingsPatch }[] = [];
  const pending = [{ baseline, draft, patch: root }];
  while (pending.length > 0) {
    const current = pending.pop()!;
    const atRoot = current.patch === root;
    for (const [key, draftValue] of Object.entries(current.draft)) {
      const baselineValue = current.baseline[key];
      if (isPlainObject(baselineValue) && isPlainObject(draftValue) && !(atRoot && wholeValueSettingsKeys.has(key))) {
        const nested: SettingsPatch = {};
        current.patch[key] = nested;
        nestedPatches.push({ parent: current.patch, key, patch: nested });
        pending.push({ baseline: baselineValue, draft: draftValue, patch: nested });
        continue;
      }

      if (!areJsonValuesEqual(baselineValue, draftValue)) {
        current.patch[key] = structuredClone(draftValue);
      }
    }
  }
  for (let index = nestedPatches.length - 1; index >= 0; index -= 1) {
    const { parent, key, patch } = nestedPatches[index]!;
    if (Object.keys(patch).length === 0) {
      delete parent[key];
    }
  }

  return root;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function areJsonValuesEqual(left: unknown, right: unknown) {
  return JSON.stringify(left) === JSON.stringify(right);
}
