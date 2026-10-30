import { For, Show } from "solid-js";
import { baseScoreWithoutOwnLayer } from "../data/softwarePolicy/baseScoreResolutionDecoder.ts";
import type { BaseScoreLayer, BaseScoreLayerSource, BaseScoreResolution } from "../types";
import { uiText } from "../text.ts";

// 软件详情 → 策略和进程：基础分当前生效的值和五层来源链（docs/scheduling_protection_2026-10-29.md §3.2、§3.3、§6）。
// 值来自后端的软件详情读取（当前发布的运行计划），这里只显示，不推算。

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

/** 软件这一级：说明下面一行生效值和来源，再列出五层，生效的那一层带标记。 */
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

/** 进程这一级：输入框下面一行生效值和来源；没有这个进程的基础分时不显示。 */
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

/** 输入框的占位：不算这个输入框自己那一层时会得到的值；没有基础分时用原来的说明文字。 */
export function baseScorePlaceholder(resolution: BaseScoreResolution | null, ownLayer: BaseScoreLayerSource, fallback: string) {
  const inherited = resolution ? baseScoreWithoutOwnLayer(resolution, ownLayer) : null;
  return inherited?.value != null
    ? uiText.softwareDetail.baseScoreSource.placeholder(formatBaseScore(inherited.value))
    : fallback;
}

/** processBaseScores 里这个进程的一项；只认自己的键，不取原型上的属性。 */
export function processBaseScore(scores: Record<string, BaseScoreResolution> | undefined, processKey: string) {
  return scores && Object.hasOwn(scores, processKey) ? scores[processKey] : null;
}
