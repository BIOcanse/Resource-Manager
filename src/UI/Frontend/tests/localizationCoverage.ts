export interface LocalizationIssue {
  language: string;
  path: string;
  kind: "missing" | "type" | "empty" | "untranslated-english" | "unexpected-han" | "simplified-chinese" | "parameter" | "consumer";
  actual: unknown;
}

// This is a coverage check, not a translation-quality score. Short shared words
// cannot generally be classified by equality (Spanish "Control" is valid).
const englishGrammar = /\b(?:the|this|that|your|you|is|are|with|and|for|to|from|not|of|in)\b/gi;
export function isEnglishProse(value: string): boolean {
  return (value.match(/[A-Za-z]+(?:['-][A-Za-z]+)*/g)?.length ?? 0) >= 4
    && (value.match(englishGrammar)?.length ?? 0) >= 2;
}

export function flattenCopy(root: unknown): Map<string, unknown> {
  const leaves = new Map<string, unknown>();
  const stack: Array<[string, unknown, ReadonlySet<object>]> = [["", root, new Set()]];
  let visited = 0;
  while (stack.length > 0) {
    if (++visited > 50_000) throw new Error("Localization tree exceeds the test traversal bound.");
    const [path, value, ancestors] = stack.pop()!;
    if (value !== null && typeof value === "object") {
      if (ancestors.has(value)) throw new Error(`Localization tree contains a cycle at ${path}.`);
      const parents = new Set(ancestors).add(value);
      for (const [key, child] of Object.entries(value)) {
        stack.push([path ? `${path}.${key}` : key, child, parents]);
      }
    } else {
      leaves.set(path, value);
    }
  }
  return leaves;
}

function isMetadata(path: string): boolean {
  // These fields are product/device names, stable option IDs, or navigation URLs.
  return /(?:^|\.)(?:id|href|language|name|productName)$/.test(path);
}

export function inspectText(language: string, path: string, actual: unknown, english: unknown, requiresTranslation = false): LocalizationIssue[] {
  const issue = (kind: LocalizationIssue["kind"]) => ({ language, path, kind, actual });
  if (typeof actual !== "string") return [issue("type")];
  if (!actual.trim()) return [issue("empty")];
  if (isMetadata(path)) return [];
  const issues: LocalizationIssue[] = [];
  if (language !== "en-US" && actual === english && (isEnglishProse(actual) || requiresTranslation)) {
    issues.push(issue("untranslated-english"));
  }
  if (!/^(?:zh|ja)-/.test(language) && /\p{Script=Han}/u.test(actual)) {
    issues.push(issue("unexpected-han"));
  }
  return issues;
}

export function inspectCopy(
  language: string, surface: string, actual: unknown, english: unknown,
  optionalDescriptions = false
): LocalizationIssue[] {
  const baseline = flattenCopy(english);
  const leaves = flattenCopy(actual);
  const issues: LocalizationIssue[] = [];
  for (const [path, expected] of baseline) {
    const fullPath = `${surface}.${path}`;
    // Only these two option contracts make description optional. Missing titles
    // and labels still fail; an explicitly present empty description also fails.
    if (optionalDescriptions && /^performance\.(?:adaptiveBooleanModeOptions|frontendHiddenRefreshModeOptions)\.\d+\.description$/.test(path) && leaves.get(path) === undefined) continue;
    if (!leaves.has(path)) {
      issues.push({ language, path: fullPath, kind: "missing", actual: null });
    } else if (typeof leaves.get(path) !== typeof expected) {
      issues.push({ language, path: fullPath, kind: "type", actual: typeof leaves.get(path) });
    }
  }
  for (const [path, value] of leaves) {
    if (typeof value === "string") issues.push(...inspectText(language, `${surface}.${path}`, value, baseline.get(path)));
  }
  return issues;
}

// Representative translated UI phrases containing Simplified-only glyphs.
// This deliberately excludes unknown device names and user-supplied strings.
export function inspectTraditional(path: string, actual: unknown): LocalizationIssue[] {
  return typeof actual === "string" && /[设监软调风转线读载应]/u.test(actual)
    ? [{ language: "zh-TW", path, kind: "simplified-chinese", actual }]
    : [];
}

export function inspectParameters(language: string, path: string, actual: unknown, parameters: readonly string[]): LocalizationIssue[] {
  return typeof actual !== "string" || parameters.some(value => !actual.includes(value))
    ? [{ language, path, kind: "parameter", actual }]
    : [];
}
