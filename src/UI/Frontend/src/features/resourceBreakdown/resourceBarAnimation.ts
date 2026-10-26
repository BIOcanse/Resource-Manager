import { animationStyleMoves, isAnimationStyleId } from "../../presentation/animationStyle.ts";

// body[data-animations] holds the effective animation style, already chosen by the automatic selection.
export function canAnimateResourceBar(effectiveStyle: string): boolean {
  return isAnimationStyleId(effectiveStyle) && animationStyleMoves(effectiveStyle);
}
