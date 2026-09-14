import { For, Show } from "solid-js";
import type { SettingsTextBundle } from "../../text";
import { dependencyAcknowledgements } from "../../i18n/dependencyAcknowledgements";

export function CreditsSettingsSection(props: { text: SettingsTextBundle }) {
  return (
    <div class="settings-credits-panel">
      <div class="settings-credit-hero">
        <strong>{props.text.credits.heroTitle}</strong>
        <p>{props.text.credits.heroBody}</p>
      </div>
      <For each={props.text.creditGroups}>
        {(group) => (
          <section class="settings-credit-group" aria-label={group.title}>
            <h3>{group.title}</h3>
            <div class="settings-credit-list">
              <For each={group.items}>
                {(item) => (
                  <article class="settings-credit-item">
                    <div class="settings-credit-title">
                      <strong>{item.name}</strong>
                      <span>{item.role}</span>
                    </div>
                    <p>{item.note}</p>
                    <Show when={item.links.length > 0}>
                      <div class="settings-credit-links">
                        <For each={item.links}>
                          {(link) => (
                            <a class="settings-credit-link" href={link.href} target="_blank" rel="noopener noreferrer">
                              {link.label}
                            </a>
                          )}
                        </For>
                      </div>
                    </Show>
                  </article>
                )}
              </For>
            </div>
          </section>
        )}
      </For>
      <details class="settings-credit-dependencies">
        <summary>{props.text.credits.dependencyListTitle} ({dependencyAcknowledgements.length})</summary>
        <p>{props.text.credits.dependencyListBody}</p>
        <ul>
          <For each={dependencyAcknowledgements}>
            {(item) => <li><strong>{item.name}</strong> {item.version} · {item.license}{" "}
              <a class="settings-credit-link" href={item.packageUrl} target="_blank" rel="noopener noreferrer">{item.packageSource}</a>
            </li>}
          </For>
        </ul>
      </details>
    </div>
  );
}
