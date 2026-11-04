import { For, Show } from "solid-js";
import { baseScoreWithoutOwnLayer } from "../data/softwarePolicy/baseScoreResolutionDecoder.ts";
import type { BaseScoreLayer, BaseScoreLayerSource, BaseScoreResolution } from "../types";
import { uiText } from "../text.ts";

// Show the effective base score and its five-layer provenance.
// Values come from the backend's published runtime plan; do not infer them locally.

function formatBaseScore(value: number) {
  return value.toFixed(value % 1 === 0 ? 0 : 1);
}

function sourceName(source: BaseScoreLayerSource) {
  return uiText.softwareDetail.baseScoreSource.sources[source];
}

function decidingLayer(resolution: BaseScoreResolution): BaseScoreLayer | null {
  return resolution.layers.find((layer) => layer.source === resolution.source) ?? null;
}

function layerText(layer: BaseScoreLayer) {
  const copy = uiText.softwareDetail.baseScoreSource;
  const name = sourceName(layer.source);
  if (layer.value === null) return copy.layer(name, copy.noValue);
  return layer.key === null
    ? copy.layer(name, formatBaseScore(layer.value))
    : copy.layerWithKey(name, formatBaseScore(layer.value), layer.key);
}

/** Software score and provenance layers, with the effective layer marked. */
export function SoftwareBaseScoreSource(props: { resolution: BaseScoreResolution }) {
  const copy = uiText.softwareDetail.baseScoreSource;
  const effectiveLine = () => {
    const resolution = props.resolution;
    const key = decidingLayer(resolution)?.key ?? null;
    const value = formatBaseScore(resolution.value);
    return key === null
      ? copy.effectiveProcess(value, sourceName(resolution.source))
      : copy.effective(value, sourceName(resolution.source), key);
  };
  return (
    <div class="software-base-score-source">
      <p class="software-detail-hint software-base-score-effective">{effectiveLine()}</p>
      <ul class="software-base-score-layers" role="list" aria-label={copy.chainLabel}>
        <For each={props.resolution.layers}>
          {(layer) => {
            const deciding = () => layer.source === props.resolution.source;
            return (
              <li
                data-source={layer.source}
                classList={{ "software-base-score-layer": true, deciding: deciding(), empty: layer.value === null }}
                aria-current={deciding() ? "true" : undefined}
              >
                <span>{layerText(layer)}</span>
                <Show when={deciding()}>
                  <span class="software-base-score-deciding">{copy.deciding}</span>
                </Show>
              </li>
            );
          }}
        </For>
      </ul>
    </div>
  );
}

/** Process score and source below the input; hidden when the process has no base score. */
export function ProcessBaseScoreLine(props: { resolution: BaseScoreResolution | null }) {
  const copy = uiText.softwareDetail.baseScoreSource;
  return (
    <Show when={props.resolution}>
      {(resolution) => (
        <p class="software-detail-hint software-base-score-effective">
          {copy.effectiveProcess(formatBaseScore(resolution().value), sourceName(resolution().source))}
        </p>
      )}
    </Show>
  );
}

/** Placeholder excludes this input's override; use the fallback text when no score exists. */
export function baseScorePlaceholder(resolution: BaseScoreResolution | null, ownLayer: BaseScoreLayerSource, fallback: string) {
  const inherited = resolution ? baseScoreWithoutOwnLayer(resolution, ownLayer) : null;
  return inherited?.value != null
    ? uiText.softwareDetail.baseScoreSource.placeholder(formatBaseScore(inherited.value))
    : fallback;
}

/** Read only the process's own key in processBaseScores, excluding inherited properties. */
export function processBaseScore(scores: Record<string, BaseScoreResolution> | undefined, processKey: string) {
  return scores && Object.hasOwn(scores, processKey) ? scores[processKey] : null;
}
