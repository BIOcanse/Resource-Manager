import { createRequire, syncBuiltinESMExports } from "node:module";

/*
 * 数浏览器门里执行了多少次 node:assert 方法调用（assert.equal、assert.ok 等），给每个旅程报告断言数，
 * 用来证明改动前后执行的断言没有变少。导入本模块即在当前进程里给 node:assert 和 node:assert/strict 的
 * 方法套一层计数，行为不变：参数原样转交，结果和异常原样返回。
 *
 * 只数 node:assert 的方法调用：直接调用 assert(...)、旅程自己写的 assert 帮助函数、throw new Error 式的
 * 检查和 Playwright 的 waitFor 都不在计数里。套了一层之后，不带消息的 assert.ok(x) 失败时自动生成的
 * 消息引用的是这一层的代码，调用位置仍在错误栈里。
 */

const countedMethods = ["ok", "equal", "notEqual", "deepEqual", "notDeepEqual", "strictEqual", "notStrictEqual",
  "deepStrictEqual", "notDeepStrictEqual", "partialDeepStrictEqual", "match", "doesNotMatch", "throws",
  "doesNotThrow", "rejects", "doesNotReject", "fail", "ifError"];

let count = 0;
const require = createRequire(import.meta.url);
const loose = require("node:assert");
for (const target of [loose, loose.strict]) {
  for (const name of countedMethods) {
    const original = target[name];
    if (typeof original !== "function") continue;
    target[name] = function countedAssertion(...args) {
      count += 1;
      return original.apply(this, args);
    };
  }
}
syncBuiltinESMExports();

/** 返回上次调用以来执行的断言数，并从零重新计数。 */
export function takeAssertionCount() {
  const taken = count;
  count = 0;
  return taken;
}
