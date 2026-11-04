import type { BaseScoreLayer, BaseScoreLayerSource, BaseScoreResolution } from "../../types.ts";
import {
  requireArray, requireFiniteNumber, requireNullable, requireOneOf, requireRecord, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/** Layer order: kind default, catalog group, catalog process, user software, user process. */
export const baseScoreLayerSources: readonly BaseScoreLayerSource[] =
  ["type_default", "catalog_group", "catalog_process", "user_software", "user_process"];

/** Validate five ordered layers; the last non-null layer must match the resolved source and value. Keys accompany values except for a kind default without software-kind context. */
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
    // Only a kind-default value without software-kind context may have no key.
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

/** Validate each processKey base score with decodeBaseScoreResolution. */
export function decodeProcessBaseScores(value: unknown, path: string): Record<string, BaseScoreResolution> {
  const row = requireRecord(value, path);
  const result: Record<string, BaseScoreResolution> = {};
  for (const [processKey, resolution] of Object.entries(row)) {
    result[processKey] = decodeBaseScoreResolution(resolution, `${path}[${JSON.stringify(processKey)}]`);
  }
  return result;
}

/** Last non-null layer before ownLayer, used as the input placeholder; null when none exists. */
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
