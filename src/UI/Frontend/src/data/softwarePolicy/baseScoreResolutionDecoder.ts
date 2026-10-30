import type { BaseScoreLayer, BaseScoreLayerSource, BaseScoreResolution } from "../../types.ts";
import {
  requireArray, requireFiniteNumber, requireNullable, requireOneOf, requireRecord, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/** 来源链的固定顺序（docs/scheduling_protection_2026-10-29.md §3.3）：类型默认 → 目录组 → 目录按进程 → 用户对软件 → 用户对进程。 */
export const baseScoreLayerSources: readonly BaseScoreLayerSource[] =
  ["type_default", "catalog_group", "catalog_process", "user_software", "user_process"];

/**
 * 软件详情读取里的一份基础分（GET /api/gpu-placement/software/{id} 的 baseScore 和 processBaseScores 的每一项）。
 * 五层按固定顺序、只认已知来源；一层的 value 是有限数或 null，key 是字符串或 null，且 key 为 null 当且仅当 value 为 null（类型默认层有值时 key 可以为 null：读取没带软件类型）；
 * source 是最后一个有值的层，value 等于那一层的值。任何一条不符都抛出，读取按失败显示。
 */
export function decodeBaseScoreResolution(value: unknown, path: string): BaseScoreResolution {
  const row = requireRecord(value, path);
  const rawLayers = requireArray(row.layers, `${path}.layers`);
  if (rawLayers.length !== baseScoreLayerSources.length) {
    throw new ResponseDecodeError(`${path}.layers`, `${baseScoreLayerSources.length} layers`);
  }
  const layers: BaseScoreLayer[] = [];
  let deciding: BaseScoreLayer | null = null;
  let index = 0;
  while (index < rawLayers.length) {
    const layerPath = `${path}.layers[${index}]`;
    const layer = requireRecord(rawLayers[index], layerPath);
    const source = requireOneOf(layer.source, `${layerPath}.source`, baseScoreLayerSources);
    if (source !== baseScoreLayerSources[index]) {
      throw new ResponseDecodeError(`${layerPath}.source`, `'${baseScoreLayerSources[index]}' at this position`);
    }
    const layerValue = requireNullable(layer.value, `${layerPath}.value`, requireFiniteNumber);
    const key = requireNullable(layer.key, `${layerPath}.key`, requireString);
    // 读取没带软件类型时，类型默认层有值但没有类型键（后端 6781a78e）；其他层有值就必须有键。
    const keyOptional = source === "type_default" && layerValue !== null;
    if (!keyOptional && (layerValue === null) !== (key === null)) {
      throw new ResponseDecodeError(`${layerPath}.key`, layerValue === null ? "null when the value is null" : "a key when the value is present");
    }
    const decoded: BaseScoreLayer = { source, value: layerValue, key };
    layers.push(decoded);
    if (layerValue !== null) deciding = decoded;
    index += 1;
  }
  if (!deciding) {
    throw new ResponseDecodeError(`${path}.layers`, "at least one layer with a value");
  }
  const source = requireOneOf(row.source, `${path}.source`, baseScoreLayerSources);
  if (source !== deciding.source) {
    throw new ResponseDecodeError(`${path}.source`, `'${deciding.source}', the last layer with a value`);
  }
  const effective = requireFiniteNumber(row.value, `${path}.value`);
  if (effective !== deciding.value) {
    throw new ResponseDecodeError(`${path}.value`, `${deciding.value}, the value of the '${deciding.source}' layer`);
  }
  return { value: effective, source, layers };
}

/** processBaseScores：processKey → 基础分；每一项按 decodeBaseScoreResolution 校验。 */
export function decodeProcessBaseScores(value: unknown, path: string): Record<string, BaseScoreResolution> {
  const row = requireRecord(value, path);
  const result: Record<string, BaseScoreResolution> = {};
  for (const [processKey, resolution] of Object.entries(row)) {
    result[processKey] = decodeBaseScoreResolution(resolution, `${path}[${JSON.stringify(processKey)}]`);
  }
  return result;
}

/** 不算用户自己这一层时的基础分：ownLayer 之前最后一个有值的层（输入框的占位）；前面都没有值时是 null。 */
export function baseScoreWithoutOwnLayer(resolution: BaseScoreResolution, ownLayer: BaseScoreLayerSource): BaseScoreLayer | null {
  const end = baseScoreLayerSources.indexOf(ownLayer);
  let found: BaseScoreLayer | null = null;
  let index = 0;
  while (index < end && index < resolution.layers.length) {
    const layer = resolution.layers[index];
    if (layer.value !== null) found = layer;
    index += 1;
  }
  return found;
}
