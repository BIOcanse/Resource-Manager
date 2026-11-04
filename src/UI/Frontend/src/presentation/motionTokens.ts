// body animation and resize attributes select motion.css tokens; :root provides desktop defaults.
// main.tsx owns observer lifetime. Read tokens from body rather than newly mounted subtrees.
import { cubicBezier } from "../interactions/protocol/ease.ts";
import type { MotionDurationToken, MotionEaseToken } from "./animationStyle.ts";

const DURATION_TOKENS: readonly MotionDurationToken[] = ["--motion-fast", "--motion-medium", "--motion-page", "--motion-panel", "--motion-bar"];
const EASE_TOKENS: readonly MotionEaseToken[] = ["--ease-decel", "--ease-standard", "--ease-bar"];
const MILLISECONDS_PER_SECOND = 1000;
const STYLE_ATTRIBUTE = "data-animations";
const RESIZING_ATTRIBUTE = "data-window-resizing";
type Ease = (progress: number) => number;
interface TokenSnapshot {
  document: Document;
  durations: Map<MotionDurationToken, number>;
  eases: Map<MotionEaseToken, Ease>;
  sync: () => void;
}
let snapshot: TokenSnapshot | null = null;

/** Read tokens before mounting; consume pending attribute records within the same task. */
export function observeMotionTokens(document: Document): () => void {
  if (snapshot) throw new Error("Motion token owner is already mounted");
  const body = document.body;
  let style: string | null | undefined;
  let resizing: string | null | undefined;
  const current: TokenSnapshot = {
    document, durations: new Map(), eases: new Map(),
    sync: () => { if (observer.takeRecords().length) refresh(); }
  };
  const refresh = () => {
    const nextStyle = body.getAttribute(STYLE_ATTRIBUTE);
    const nextResizing = body.getAttribute(RESIZING_ATTRIBUTE);
    if (nextStyle === style && nextResizing === resizing) return;
    const computed = getComputedStyle(body);
    current.durations = readDurations(computed);
    current.eases = new Map(EASE_TOKENS.map((token) => [token, parseEase(token, computed.getPropertyValue(token).trim())]));
    style = nextStyle;
    resizing = nextResizing;
  };
  const observer = new MutationObserver(refresh);
  refresh();
  observer.observe(body, { attributes: true, attributeFilter: [STYLE_ATTRIBUTE, RESIZING_ATTRIBUTE] });
  snapshot = current;
  return () => { observer.disconnect(); snapshot = null; };
}
function readDurations(style: CSSStyleDeclaration): Map<MotionDurationToken, number> {
  return new Map(DURATION_TOKENS.map((token) => {
    const text = style.getPropertyValue(token).trim();
    const match = /^(-?\d*\.?\d+)(ms|s)$/.exec(text);
    if (!match) throw new Error(`Motion token ${token} is not a duration: "${text}"`);
    return [token, Number(match[1]) * (match[2] === "s" ? MILLISECONDS_PER_SECOND : 1)];
  }));
}
/** cubic-bezier(x1, y1, x2, y2), or steps(n, end): floor(p * n) / n, with p = 1 yielding 1. */
function parseEase(token: MotionEaseToken, text: string): Ease {
  const steps = /^steps\(\s*(\d+)\s*(?:,\s*(?:end|jump-end)\s*)?\)$/.exec(text);
  if (steps) {
    const count = Number(steps[1]);
    if (count > 0) return (progress) => (progress >= 1 ? 1 : Math.floor(progress * count) / count);
  }
  const match = /^cubic-bezier\(\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)$/.exec(text);
  const points = match ? match.slice(1).map(Number) : [];
  if (points.length !== 4 || points.some((point) => !Number.isFinite(point))) {
    throw new Error(`Motion token ${token} is not a cubic-bezier or steps(n, end): "${text}"`);
  }
  return cubicBezier(points[0], points[1], points[2], points[3]);
}
function tokensFor(element: Element): TokenSnapshot {
  if (!snapshot || snapshot.document !== element.ownerDocument) throw new Error("Motion token owner is not mounted for this document");
  snapshot.sync();
  return snapshot;
}
export function readMotionDurationMs(element: Element, token: MotionDurationToken): number {
  return tokensFor(element).durations.get(token)!;
}
export function readMotionEase(element: Element, token: MotionEaseToken): Ease {
  return tokensFor(element).eases.get(token)!;
}
