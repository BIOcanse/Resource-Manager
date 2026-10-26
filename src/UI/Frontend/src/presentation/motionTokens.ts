// styles/motion.css 的令牌只由根元素手感和 body 窗口缩放属性定义。
// main.tsx 显式建立、释放观察者；调用方不再读取新挂载子树的样式。
import { cubicBezier } from "../interactions/protocol/ease.ts";
import type { MotionDurationToken, MotionEaseToken } from "./motionFeel.ts";

export type MotionReadableEaseToken = MotionEaseToken | "--ease-bar";
const DURATION_TOKENS: readonly MotionDurationToken[] = ["--motion-fast", "--motion-medium", "--motion-page", "--motion-panel", "--motion-bar"];
const EASE_TOKENS: readonly MotionReadableEaseToken[] = ["--ease-decel", "--ease-standard", "--ease-bar"];
const MILLISECONDS_PER_SECOND = 1000;
const FEEL_ATTRIBUTE = "data-motion-feel";
const RESIZING_ATTRIBUTE = "data-window-resizing";
type Ease = (progress: number) => number;
interface TokenSnapshot {
  document: Document;
  rootDurations: Map<MotionDurationToken, number>;
  bodyDurations: Map<MotionDurationToken, number>;
  eases: Map<MotionReadableEaseToken, Ease>;
  sync: () => void;
}
let snapshot: TokenSnapshot | null = null;

/** 挂载前读取定义处；同一任务内的读取也消费尚未投递的属性记录。 */
export function observeMotionTokens(document: Document): () => void {
  if (snapshot) throw new Error("Motion token owner is already mounted");
  const root = document.documentElement;
  const body = document.body;
  let feel: string | null | undefined;
  let resizing: string | null | undefined;
  const current: TokenSnapshot = {
    document, rootDurations: new Map(), bodyDurations: new Map(), eases: new Map(),
    sync: () => { if (observer.takeRecords().length) refresh(); }
  };
  const refresh = () => {
    const nextFeel = root.getAttribute(FEEL_ATTRIBUTE);
    const nextResizing = body.getAttribute(RESIZING_ATTRIBUTE);
    const feelChanged = nextFeel !== feel;
    if (feelChanged) {
      const style = getComputedStyle(root);
      current.rootDurations = readDurations(style);
      current.eases = new Map(EASE_TOKENS.map((token) => [token, parseEase(token, style.getPropertyValue(token).trim())]));
    }
    if (feelChanged || nextResizing !== resizing) {
      current.bodyDurations = nextResizing === "yes" ? readDurations(getComputedStyle(body)) : current.rootDurations;
    }
    feel = nextFeel;
    resizing = nextResizing;
  };
  const observer = new MutationObserver(refresh);
  refresh();
  observer.observe(root, { attributes: true, attributeFilter: [FEEL_ATTRIBUTE] });
  observer.observe(body, { attributes: true, attributeFilter: [RESIZING_ATTRIBUTE] });
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
function parseEase(token: MotionReadableEaseToken, text: string): Ease {
  const match = /^cubic-bezier\(\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)$/.exec(text);
  const points = match ? match.slice(1).map(Number) : [];
  if (points.length !== 4 || points.some((point) => !Number.isFinite(point))) {
    throw new Error(`Motion token ${token} is not a cubic-bezier: "${text}"`);
  }
  return cubicBezier(points[0], points[1], points[2], points[3]);
}
function tokensFor(element: Element): TokenSnapshot {
  if (!snapshot || snapshot.document !== element.ownerDocument) throw new Error("Motion token owner is not mounted for this document");
  snapshot.sync();
  return snapshot;
}
export function readMotionDurationMs(element: Element, token: MotionDurationToken): number {
  const tokens = tokensFor(element);
  return (element === tokens.document.documentElement ? tokens.rootDurations : tokens.bodyDurations).get(token)!;
}
export function readMotionEase(element: Element, token: MotionReadableEaseToken): Ease {
  return tokensFor(element).eases.get(token)!;
}
