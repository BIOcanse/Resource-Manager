import { For, Show, createMemo, createSignal } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import { dependencyAcknowledgements } from "../../../i18n/dependencyAcknowledgements";
import { Disclosure } from "../../../ui/primitives/Disclosure.tsx";

/*
 * 致谢页（规则见 docs/credits_sources.md）：
 * - 主列表是一张表：名称与分组 | 子项目的官方原文 | 按钮列。列定义在 .settings-credit-table 上，分组、列表、每一项逐层继承，
 *   所以各分组之间的列也对齐。每个子项目一行，按钮按固定顺序各占一列，同一列的按钮 x 相同；
 * - 完整依赖鸣谢是一张四列表（名称、版本、许可证、来源），放在折叠标题里。
 */

// 鼠标点开外部链接后不留焦点：窗口回到前台时链接不会带着焦点环。键盘激活（detail 为 0）照常保留焦点。
function releasePointerFocus(event: MouseEvent & { currentTarget: HTMLAnchorElement }) {
  if (event.detail > 0) event.currentTarget.blur();
}

export function CreditsSettingsSection(props: { text: SettingsTextBundle }) {
  const [dependenciesOpen, setDependenciesOpen] = createSignal(false);
  // 按钮列数：一行里按钮最多的子项目有几个按钮。
  const linkColumns = createMemo(() => Math.max(1, ...props.text.creditGroups
    .flatMap((group) => group.items.flatMap((item) => item.subjects.map((subject) => subject.links.length)))));
  return (
    <div class="settings-credits-panel" style={{ "--credit-link-columns": linkColumns() }}>
      <div class="settings-credit-hero">
        <strong>{props.text.credits.heroTitle}</strong>
        <p>{props.text.credits.heroBody}</p>
      </div>
      <div class="settings-credit-table">
      <For each={props.text.creditGroups}>
        {(group) => (
          <section class="settings-credit-group" aria-label={group.title}>
            <h3>{group.title}</h3>
            <div class="settings-credit-list">
              <For each={group.items}>
                {(item) => (
                  <article class="settings-credit-item" style={{ "--credit-subjects": item.subjects.length }}>
                    <div class="settings-credit-title">
                      <strong title={item.name}>{item.name}</strong>
                      <span>{item.role}</span>
                    </div>
                    <For each={item.subjects}>
                      {(subject) => <>
                        <div class="settings-credit-subject">
                          <Show when={subject.name}>{(name) => <strong>{name()}</strong>}</Show>
                          <Show when={subject.description}>{(description) => <p>
                            <q cite={description().source}>{description().text}</q>{" "}
                            <a class="settings-credit-source" href={description().source} title={description().source}
                              target="_blank" rel="noopener noreferrer" onClick={releasePointerFocus}>
                              {props.text.credits.sourceLabel}
                            </a>
                          </p>}</Show>
                        </div>
                        <For each={subject.links}>
                          {(link, index) => (
                            <a class="settings-credit-link" style={{ "--credit-link-column": index() + 1 }} href={link.href}
                              aria-label={link.accessibleName} title={link.accessibleName}
                              target="_blank" rel="noopener noreferrer" onClick={releasePointerFocus}>
                              {link.label}
                            </a>
                          )}
                        </For>
                      </>}
                    </For>
                  </article>
                )}
              </For>
            </div>
          </section>
        )}
      </For>
      </div>
      <Disclosure name="完整依赖鸣谢" class="settings-credit-dependencies" expanded={dependenciesOpen()}
        onToggle={() => setDependenciesOpen(!dependenciesOpen())}
        header={<>{props.text.credits.dependencyListTitle} <span class="settings-credit-count">({dependencyAcknowledgements.length})</span></>}>
        <p>{props.text.credits.dependencyListBody}</p>
        <div class="settings-credit-dependency-scroll" role="region" tabindex="0" aria-label={props.text.credits.dependencyListTitle}>
          <table class="settings-credit-dependency-table">
            <thead>
              <tr>
                <th scope="col">{props.text.credits.dependencyColumns.name}</th>
                <th scope="col">{props.text.credits.dependencyColumns.version}</th>
                <th scope="col">{props.text.credits.dependencyColumns.license}</th>
                <th scope="col">{props.text.credits.dependencyColumns.source}</th>
              </tr>
            </thead>
            <tbody>
              <For each={dependencyAcknowledgements}>
                {(item) => <tr>
                  <th scope="row">{item.name}</th>
                  <td class="settings-credit-number">{item.version}</td>
                  <td class="settings-credit-number">{item.license}</td>
                  <td>
                    <a class="settings-credit-link" href={item.packageUrl}
                      aria-label={`${props.text.credits.linkLabels[item.packageKind]} · ${item.name}`}
                      target="_blank" rel="noopener noreferrer" onClick={releasePointerFocus}>
                      {props.text.credits.linkLabels[item.packageKind]}
                    </a>
                  </td>
                </tr>}
              </For>
            </tbody>
          </table>
        </div>
      </Disclosure>
    </div>
  );
}
