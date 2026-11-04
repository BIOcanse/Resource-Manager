import { For, Show, createMemo, createSignal } from "solid-js";
import { creditLinkKinds } from "../../../i18n/settingsTypes.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import { dependencyAcknowledgements } from "../../../i18n/dependencyAcknowledgements";
import { Disclosure } from "../../../ui/primitives/Disclosure.tsx";

/* Share table columns across credit groups. Include only used link kinds and keep each kind aligned; list full dependencies in a separate disclosure. */

// Blur pointer-activated external links while preserving keyboard focus.
function releasePointerFocus(event: MouseEvent & { currentTarget: HTMLAnchorElement }) {
  if (event.detail > 0) event.currentTarget.blur();
}

export function CreditsSettingsSection(props: { text: SettingsTextBundle }) {
  const [dependenciesOpen, setDependenciesOpen] = createSignal(false);
  // Create one named column per used link kind in fixed order.
  const linkTracks = createMemo(() => {
    const used = new Set(props.text.creditGroups.flatMap((group) => group.items.flatMap((item) =>
      item.subjects.flatMap((subject) => subject.links.map((link) => link.kind)))));
    return creditLinkKinds.filter((kind) => used.has(kind)).map((kind) => `[${kind}] max-content`).join(" ");
  });
  return (
    <div class="settings-credits-panel" style={{ "--credit-link-tracks": linkTracks() }}>
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
                      <strong data-full-content={item.name}>{item.name}</strong>
                      <span>{item.role}</span>
                    </div>
                    <For each={item.subjects}>
                      {(subject) => <>
                        <div class="settings-credit-subject">
                          <Show when={subject.name}>{(name) => <strong>{name()}</strong>}</Show>
                          <Show when={subject.description}>{(description) => <p>
                            <q cite={description().source}>{description().text}</q>
                            <a class="settings-credit-source" href={description().source} data-full-content={description().source}
                              target="_blank" rel="noopener noreferrer" onClick={releasePointerFocus}>
                              {props.text.credits.sourceLabel}
                            </a>
                          </p>}</Show>
                        </div>
                        <For each={subject.links}>
                          {(link) => (
                            <a class="settings-credit-link" data-kind={link.kind} style={{ "grid-column": link.kind }} href={link.href}
                              aria-label={link.accessibleName} data-name-tooltip=""
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
