// 软件详情读取里的基础分（后端 CompiledBaseScorePlan.Resolve，GET /api/gpu-placement/software/{id}）。
// 五层固定顺序；没有值的层 value 和 key 都是 null；生效的是最后一个有值的层。
// 类型默认的值同后端 OptimizationRuntimeScoringDefaults.BaseScoreForKind，键是请求里的 softwareKind（没传时为 null，同后端）。
const typeDefaults = new Map([
  ["game", 95], ["high_performance", 85], ["windows_system", 60], ["windows_component", 60], ["windows_service", 60],
  ["adapted", 80], ["controlled", 45], ["managed", 42], ["unattributed", 30]
]);

export function mockTypeBaseScore(kind) {
  return typeDefaults.get(kind ?? "") ?? 35;
}

/** layers：{ type_default, catalog_group, catalog_process, user_software, user_process } 各层的 { value, key }，缺省为没有值。 */
export function baseScoreResolution(layers) {
  const resolved = ["type_default", "catalog_group", "catalog_process", "user_software", "user_process"].map(source => ({
    source, value: layers[source]?.value ?? null, key: layers[source]?.value == null ? null : layers[source].key ?? null
  }));
  const deciding = resolved.findLast(layer => layer.value !== null);
  return { value: deciding.value, source: deciding.source, layers: resolved };
}

/** 模拟后端的软件这一级：没有软件目录，只有类型默认和用户对软件的设置。 */
export function mockBaseScoreResolution(softwareId, kind, softwareOverride) {
  return baseScoreResolution({
    type_default: { value: mockTypeBaseScore(kind), key: kind ?? null },
    user_software: softwareOverride == null ? undefined : { value: softwareOverride, key: softwareId }
  });
}
