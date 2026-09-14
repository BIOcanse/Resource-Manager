import { For, Show } from "solid-js";
import { AlertTriangle } from "lucide-solid";
import type { SoftwareIssueTag } from "../types";

export function SoftwareIssueTagStrip(props: {
  issues?: readonly SoftwareIssueTag[] | null;
}) {
  return (
    <Show when={(props.issues?.length ?? 0) > 0}>
      <div class="software-issue-tag-strip" aria-label="软件问题">
        <For each={props.issues ?? []}>
          {(issue) => (
            <span
              class={`software-issue-tag ${issueToneClass(issue.severity)}`}
              title={issue.message}
            >
              {issue.label}
            </span>
          )}
        </For>
      </div>
    </Show>
  );
}

export function SoftwareIssueDetailSection(props: {
  issues?: readonly SoftwareIssueTag[] | null;
}) {
  return (
    <Show when={(props.issues?.length ?? 0) > 0}>
      <section class="software-detail-section software-issue-section">
        <h3><AlertTriangle aria-hidden="true" size={18} /> 问题提示</h3>
        <div class="software-issue-detail-list">
          <For each={props.issues ?? []}>
            {(issue) => (
              <article class="software-issue-detail-item">
                <div class="software-issue-detail-heading">
                  <span class={`software-issue-tag ${issueToneClass(issue.severity)}`}>
                    {issue.label}
                  </span>
                  <span class="software-issue-source">
                    {issue.dynamic ? "当前报告" : "已知问题目录"}
                  </span>
                </div>
                <p>{issue.message}</p>
                <Show when={(issue.references?.length ?? 0) > 0}>
                  <div class="software-issue-references" aria-label={`${issue.label}参考资料`}>
                    <For each={issue.references ?? []}>
                      {(reference) => (
                        <a href={reference.url} target="_blank" rel="noreferrer">
                          {reference.label}
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
    </Show>
  );
}

function issueToneClass(severity: string) {
  switch (severity) {
    case "Critical":
      return "critical";
    case "Warning":
      return "warning";
    default:
      return "info";
  }
}
