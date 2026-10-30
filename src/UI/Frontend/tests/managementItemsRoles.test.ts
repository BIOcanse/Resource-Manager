import assert from "node:assert/strict";
import { getManagementItems } from "../src/features/management/managementItems.ts";
import type { SoftwareRecord } from "../src/types.ts";

/*
 * 回归（10-30，已安装候选 .4）：RM 自己的依赖组件以 kind "managed"、managementRole "dependency" 的软件记录到达，
 * 应用分类按 kind 归一化时落进“一般应用”，和依赖页重复，还带着“一键卸载”。
 * 带角色的记录只列在所属角色那一页。
 */

const record = (id: string, kind: string, managementRole?: "dependency" | "support" | null): SoftwareRecord =>
  ({ id, name: id, kind, managementRole, identityConfirmed: true }) as SoftwareRecord;

const software = [
  record("dependency:shared-webview2-runtime", "managed", "dependency"),
  record("dependency:hardware-sensors-pawnio-provider", "managed", "dependency"),
  record("support:helper", "other", "support"),
  record("app:editor", "other", null),
  record("app:legacy", "other"),
  record("game:one", "game")
];
const ids = (kind: Parameters<typeof getManagementItems>[0]) =>
  getManagementItems(kind, [], software).map((item) => item.value.id);

assert.deepEqual(ids("other"), ["app:editor", "app:legacy"], "general apps leave out role records");
assert.deepEqual(ids("game"), ["game:one"]);
assert.deepEqual(ids("Dependency"), ["dependency:shared-webview2-runtime", "dependency:hardware-sensors-pawnio-provider"],
  "dependency records stay on the dependency page");
assert.deepEqual(ids("Support"), ["support:helper"]);

const unclassified = { ...record("dependency:unclassified", "managed", "dependency"), issues: [{ kind: "unclassified" }] } as SoftwareRecord;
assert.deepEqual(getManagementItems("Unclassified", [], [unclassified]).map((item) => item.value.id), [],
  "role records are never asked to be classified as apps");

console.log("management items roles: ok");
