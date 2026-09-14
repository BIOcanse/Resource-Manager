import { createSignal, For, JSX, Show } from "solid-js";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import type {
  DiscoverySession,
  MigrationCandidate,
  MigrationKind,
  MigrationPlan,
  MigrationRoots,
  MigrationTargetCategory,
  SoftwareDataMigrationRecord
} from "../types";
import { StandardSelect } from "./StandardSelect";
import { UserDetailsDialog } from "./UserDetailsDialog";
import {
  migrationClassificationLabel,
  migrationKindLabel,
  migrationRecordDetails,
  migrationStateLabel,
  migrationTargetCategoryLabel,
  userFacingDateTime,
  userFacingRisk
} from "../presentation/userFacingText";
import type { UserDetailSection } from "../presentation/userDetails";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../presentation/userDetails";
import { formatBytes } from "../utils";

interface MigrationPanelProps {
  status: string;
  softwareName: string;
  kind: MigrationKind | string;
  targetCategory: MigrationTargetCategory | string;
  sourcePaths: string;
  discoveryProgramRootPaths: string;
  discoveryProcessNames: string;
  allowMediumRisk: boolean;
  roots: MigrationRoots | null;
  plan: MigrationPlan | null;
  records: SoftwareDataMigrationRecord[];
  sessions: DiscoverySession[];
  candidates: MigrationCandidate[];
  activeSessionId: string | null;
  workbenchAvailable: boolean;
  operationActionsAvailable: boolean;
  previewInProgress: boolean;
  candidateLookupInProgress: boolean;
  executeInProgress: boolean;
  discoveryStartInProgress: boolean;
  discoveryStopInProgress: boolean;
  isRestoreInProgress: (recordId: string) => boolean;
  setSoftwareName: (value: string) => void;
  setKind: (value: string) => void;
  setTargetCategory: (value: string) => void;
  setSourcePaths: (value: string) => void;
  setDiscoveryProgramRootPaths: (value: string) => void;
  setDiscoveryProcessNames: (value: string) => void;
  setAllowMediumRisk: (value: boolean) => void;
  onPreview: () => void;
  onExecute: () => void;
  onFindCandidates: () => void;
  onStartDiscovery: () => void;
  onStopDiscovery: () => void;
  onUseCandidate: (candidate: MigrationCandidate) => void;
  onMigrateCandidate: (candidate: MigrationCandidate) => void;
  onRestore: (record: SoftwareDataMigrationRecord) => void;
}

export function MigrationPanel(props: MigrationPanelProps) {
  const demandId = "management.migration.workbench";
  useFrontendVisibilityDemand(demandId, [
    frontendWorkIds.migrationRoots,
    frontendWorkIds.migrationRecords,
    frontendWorkIds.migrationSessions
  ]);
  const [details, setDetails] = createSignal<{ title: string; summary?: string; sections: UserDetailSection[] } | null>(null);
  return (
    <section
      {...frontendVisibilitySurface(
        "visible.management.migration.workbench.surface",
        [demandId])}
      class="migration-panel"
      aria-busy={props.previewInProgress
        || props.candidateLookupInProgress
        || props.executeInProgress
        || props.discoveryStartInProgress
        || props.discoveryStopInProgress}
    >
      <div class="panel-header">
        <h2>迁移工作台</h2>
        <span id="migrationStatus" role="status" aria-live="polite">{props.status}</span>
      </div>
      <div class="migration-form">
        <label>
          <span>软件名</span>
          <input value={props.softwareName} type="text" placeholder="例如 MSI Afterburner" onInput={(event) => props.setSoftwareName(event.currentTarget.value)} />
        </label>
        <label>
          <span>迁移类型</span>
          <StandardSelect
            value={props.kind}
            ariaLabel="迁移类型"
            options={[
              { value: "Data", label: "数据" },
              { value: "Root", label: "根目录" }
            ]}
            onChange={props.setKind}
          />
        </label>
        <label>
          <span>目标分类</span>
          <StandardSelect
            value={props.targetCategory}
            ariaLabel="目标分类"
            options={[
              { value: "UserData", label: "用户数据" },
              { value: "Misc", label: "其他数据" }
            ]}
            onChange={props.setTargetCategory}
          />
        </label>
        <label class="migration-paths-field">
          <span>源路径</span>
          <textarea value={props.sourcePaths} rows={3} placeholder="每行一个目录，例如 %LOCALAPPDATA%\\SomeApp" onInput={(event) => props.setSourcePaths(event.currentTarget.value)} />
        </label>
        <label class="migration-risk-toggle">
          <input type="checkbox" checked={props.allowMediumRisk} onChange={(event) => props.setAllowMediumRisk(event.currentTarget.checked)} />
          <span>允许中等风险迁移（含软件根目录）</span>
        </label>
      </div>
      <div class="migration-discovery">
        <label>
          <span>手动根目录（高级）</span>
          <textarea value={props.discoveryProgramRootPaths} rows={2} placeholder="通常不填。未知软件才手动添加，每行一个根目录" onInput={(event) => props.setDiscoveryProgramRootPaths(event.currentTarget.value)} />
        </label>
        <label>
          <span>需要观察的进程名称</span>
          <input value={props.discoveryProcessNames} type="text" placeholder="可选，例如 app.exe, helper.exe" onInput={(event) => props.setDiscoveryProcessNames(event.currentTarget.value)} />
        </label>
        <div class="migration-actions">
          <button
            class="secondary"
            type="button"
            disabled={!props.workbenchAvailable || props.candidateLookupInProgress}
            onClick={props.onFindCandidates}
          >
            {props.candidateLookupInProgress ? "正在查找" : "查找可迁移内容"}
          </button>
          <button
            class="secondary"
            type="button"
            disabled={!props.operationActionsAvailable
              || props.discoveryStartInProgress
              || Boolean(props.activeSessionId)}
            onClick={props.onStartDiscovery}
          >
            {props.discoveryStartInProgress ? "正在启动" : "开始监控"}
          </button>
          <button
            class="secondary"
            type="button"
            disabled={!props.operationActionsAvailable
              || !props.activeSessionId
              || props.discoveryStopInProgress}
            onClick={props.onStopDiscovery}
          >
            {props.discoveryStopInProgress ? "正在停止" : "停止监控"}
          </button>
        </div>
        <ListBlock className="discovery-sessions" items={props.sessions} empty="" render={(session) => (
          <div class="discovery-item">
            <div>
              <strong>{session.softwareName || "未命名软件"}</strong>
              <div class="discovery-meta">{migrationStateLabel(session.state)} · {session.observedWriteCount} 次变化</div>
            </div>
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({
                title: `${session.softwareName || "软件"}监控详情`,
                sections: discoverySessionDetails(session)
              })}
            >
              详细信息
            </button>
          </div>
        )} />
        <ListBlock className="discovery-candidates" items={props.candidates} empty="" render={(candidate) => (
          <div class="discovery-item">
            <strong>{candidate.path ?? candidate.directory ?? candidate.name}</strong>
            <div class="migration-actions">
              <button
                class="secondary details-button"
                type="button"
                onClick={() => setDetails({ title: "可迁移内容详情", sections: migrationCandidateDetails(candidate) })}
              >
                详细信息
              </button>
              <button class="secondary" type="button" onClick={() => props.onUseCandidate(candidate)}>填入</button>
              <button
                type="button"
                disabled={!props.workbenchAvailable || props.previewInProgress}
                onClick={() => props.onMigrateCandidate(candidate)}
              >
                迁移
              </button>
            </div>
          </div>
        )} />
      </div>
      <div class="migration-actions">
        <button
          class="secondary"
          type="button"
          disabled={!props.workbenchAvailable || props.previewInProgress}
          onClick={props.onPreview}
        >
          {props.previewInProgress ? "正在预览" : "预览"}
        </button>
        <button
          type="button"
          disabled={!props.operationActionsAvailable
            || !props.plan?.canExecute
            || props.executeInProgress}
          onClick={props.onExecute}
        >
          {props.executeInProgress ? "正在执行" : "执行迁移"}
        </button>
      </div>
      <div class="migration-roots">
        <Show when={props.roots}>
          {(roots) => (
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({ title: "迁移保存位置", sections: migrationRootDetails(roots()) })}
            >
              查看保存位置
            </button>
          )}
        </Show>
      </div>
      <div class="migration-plan">
        <Show when={props.plan}>
          {(plan) => (
            <>
              <div>{plan().canExecute ? "迁移方案已准备完成。" : "部分项目需要处理后才能迁移。"}</div>
              <For each={plan().items ?? []}>
                {(item) => (
                  <div class="migration-item">
                    <strong>{userFacingRisk(item.risk)} · {migrationClassificationLabel(item.classification)} · {item.canExecute ? "可以迁移" : "需要处理"}</strong>
                    <code>{item.sourcePath}</code>
                    <button
                      class="secondary details-button"
                      type="button"
                      onClick={() => setDetails({ title: "迁移项目详情", sections: migrationPlanItemDetails(item) })}
                    >
                      详细信息
                    </button>
                  </div>
                )}
              </For>
            </>
          )}
        </Show>
      </div>
      <div class="panel-header migration-record-header">
        <h2>恢复</h2>
        <span id="migrationRecordCount">{props.records.length} 条记录</span>
      </div>
      <ListBlock className="migration-records" items={props.records} empty="" render={(record) => (
        <div class="migration-record">
          <div>
            <strong>{record.softwareName || "未命名软件"}</strong>
            <span>{migrationStateLabel(record.state)}</span>
          </div>
          <div class="migration-actions">
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({ title: "迁移记录详情", sections: migrationRecordDetails(record) })}
            >
              详细信息
            </button>
            <button
              class="secondary"
              type="button"
              disabled={!props.operationActionsAvailable || props.isRestoreInProgress(record.id)}
              onClick={() => props.onRestore(record)}
            >
              {props.isRestoreInProgress(record.id) ? "正在恢复" : "恢复"}
            </button>
          </div>
        </div>
      )} />
      <UserDetailsDialog
        open={details() !== null}
        title={details()?.title ?? "详细信息"}
        summary={details()?.summary}
        sections={details()?.sections ?? []}
        onClose={() => setDetails(null)}
      />
    </section>
  );
}

function discoverySessionDetails(session: DiscoverySession) {
  return compactUserDetailSections([
    userDetailSection("监控状态", [
      userDetailItem("软件", session.softwareName || "未命名软件"),
      userDetailItem("状态", migrationStateLabel(session.state)),
      userDetailItem("开始时间", userFacingDateTime(session.startedAt)),
      session.stoppedAt ? userDetailItem("停止时间", userFacingDateTime(session.stoppedAt)) : null,
      userDetailItem("发现变化", `${session.observedWriteCount} 次`),
      session.processNames?.length ? userDetailItem("观察进程", session.processNames.join("、")) : null
    ]),
    userDetailSection("观察范围", (session.programRootPaths ?? []).map((path, index) => userDetailItem(`目录 ${index + 1}`, path)))
  ]);
}

function migrationCandidateDetails(candidate: MigrationCandidate) {
  return compactUserDetailSections([
    userDetailSection("可迁移内容", [
      userDetailItem("位置", candidate.path ?? candidate.directory ?? candidate.name),
      userDetailItem("建议内容", migrationKindLabel(candidate.recommendedMigrationKind)),
      userDetailItem("建议保存位置", migrationTargetCategoryLabel(candidate.recommendedTargetCategory)),
      typeof candidate.observedWriteCount === "number" ? userDetailItem("发现变化", `${candidate.observedWriteCount} 次`) : null,
      candidate.lastObservedAt ? userDetailItem("最近发现", userFacingDateTime(candidate.lastObservedAt)) : null,
      candidate.processNames?.length ? userDetailItem("相关进程", candidate.processNames.join("、")) : null
    ])
  ]);
}

function migrationRootDetails(roots: MigrationRoots) {
  return compactUserDetailSections([
    userDetailSection("保存位置", [
      userDetailItem("用户数据", roots.userDataRoot),
      userDetailItem("其他数据", roots.miscRoot),
      userDetailItem("软件根目录", roots.managedSoftwareRoot)
    ])
  ]);
}

function migrationPlanItemDetails(item: MigrationPlan["items"][number]) {
  return compactUserDetailSections([
    userDetailSection("迁移判断", [
      userDetailItem("风险", userFacingRisk(item.risk)),
      userDetailItem("内容", migrationClassificationLabel(item.classification)),
      userDetailItem("结果", item.canExecute ? "可以迁移" : "需要调整后再迁移"),
      typeof item.sizeBytes === "number" ? userDetailItem("大小", formatBytes(item.sizeBytes)) : null
    ]),
    userDetailSection("位置", [
      userDetailItem("原位置", item.sourcePath),
      userDetailItem("迁移后位置", item.destinationPath)
    ])
  ]);
}

function ListBlock<T>(props: {
  className: string;
  items: T[];
  empty: string;
  render: (item: T) => JSX.Element;
}) {
  return (
    <div class={props.className}>
      <Show when={props.items.length > 0} fallback={props.empty ? <div>{props.empty}</div> : null}>
        <For each={props.items}>{props.render}</For>
      </Show>
    </div>
  );
}
