import assert from "node:assert/strict";
import { canAnimateResourceBar } from "../src/features/resourceBreakdown/resourceBarAnimation.ts";
// 资源条只在会动的动画风格下有过渡；不认识的值（发布风格之前 body 上还没有属性）不动。
for (const style of ["smooth", "desktop", "terminal"]) assert.equal(canAnimateResourceBar(style), true, style);
for (const style of ["none", "ultra", "normal", ""]) assert.equal(canAnimateResourceBar(style), false, style);
