import { For } from "solid-js";

/**
 * 会切换文字的按钮标签（刷新 / 刷新中，编辑 / 保存 / 保存中）。
 *
 * 所有候选文字叠在同一格里，只显示 current 那一个，其余用 visibility: hidden 占位：
 * 按钮宽度始终是当前语言里最长候选的宽度，切换时按钮和它旁边的东西都不挪动。
 * 隐藏的候选不进入无障碍名称，也不进入 innerText。
 */
export function StableLabel<K extends string>(props: {
  readonly labels: Readonly<Record<K, string>>;
  readonly current: K;
}) {
  return (
    <span class="stable-label">
      <For each={Object.keys(props.labels) as K[]}>
        {(key) => (
          <span classList={{ "stable-label-inactive": key !== props.current }}>
            {props.labels[key]}
          </span>
        )}
      </For>
    </span>
  );
}
