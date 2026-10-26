import { componentCapabilityLabel, componentInstallNote, componentNeedsUpdate, componentVerifyLabel } from "../../../presentation/componentPresentation.ts";
import { createEffect, createMemo, createSignal, For, on, Show } from "solid-js";
import { AlertTriangle, Check, Info, MoreHorizontal, Settings } from "lucide-solid";
import { sharedBrowserRuntimeComponentId } from "../../browserRuntimes/browserRuntimeTypes";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import {
  frontendVisibilityDemandId,
  frontendVisibilitySurface
} from "../../../frontendWork/frontendVisibilitySurface";
import {
  FrontendVisibilityDemandBinding,
  useFrontendVisibilityDemand
} from "../../../frontendWork/useFrontendVisibilityDemand";
import { componentDisplayName, componentPurpose, userFacingState } from "../../../presentation/userFacingText";
import type { SoftwareContextMenuTarget } from "../../../components/SoftwareContextMenu";
import {
  browserRuntimeManagementSubpageId,
  managementInventoryKinds,
  type ManagementSubpageId
} from "../managementNavigation";
import type { ManagedComponent, ManagementKind, ManagementRole, OperationSnapshot, SoftwareRecord } from "../../../types";
import { managementKindLabel, softwareDisplayKindLabel, uiText } from "../../../text.ts";
import { isComponentInstalled, normalizeName, normalizeSoftwareKind } from "../../../utils";
import {
  observationCanRender,
  type ObservationState
} from "../../../observation/observationState";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../../../components/ObservationStateNotice";
import { SoftwareIssueTagStrip } from "../../../components/SoftwareIssuePresentation";
import { isUnclassifiedSoftware, runtimeDiscoveryLabel, softwareIssueLabel, softwareIssueMessage } from "../../../presentation/softwareClassification.ts";
import { renderBackendMessage } from "../../../presentation/backendMessage.ts";
import { softwareRecordName, softwareSourceLabels, softwareUninstallDescription, softwareSummary } from "../../../presentation/softwareFacts.ts";
export { softwareSummary } from "../../../presentation/softwareFacts.ts";

// 分类名按当前语言求值，不能在模块顶层固化。
export function managementKinds(): Array<{ id: ManagementKind; label: string }> {
  return managementInventoryKinds.map((id) => ({
    id,
    label: managementKindLabel(id)
  }));
}

export interface ManagementItem {
  type: "component" | "software";
  value: ManagedComponent | SoftwareRecord;
}

interface ManagementPageProps {
  activeKind: ManagementKind;
  components: ManagedComponent[];
  software: SoftwareRecord[];
  actionLabels: Record<string, string>;
  observation: ObservationState;
  softwareObservation: ObservationState;
  operationsObservation: ObservationState;
  refreshInProgress: boolean;
  mutablePersistenceEnabled: boolean;
  runtimeEffectsEnabled: boolean;
  onAddManualSoftware: (kind: ManagementKind) => void;
  onClassifySoftware: (software: SoftwareRecord) => void;
  onRefresh: () => void;
  onInstallComponent: (component: ManagedComponent) => void;
  onVerifyComponent: (component: ManagedComponent) => void;
  onRemoveManual: (software: SoftwareRecord) => void;
  onUninstallSoftware: (software: SoftwareRecord, actionKey?: string) => void;
  onOpenDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
}

interface ManagementSubpageBarProps {
  activeSubpage: ManagementSubpageId;
  components: ManagedComponent[];
  software: SoftwareRecord[];
  mutablePersistenceEnabled: boolean;
  runtimeEffectsEnabled: boolean;
  onSubpageChange: (subpage: ManagementSubpageId) => void;
}

export function ManagementSubpageBar(props: ManagementSubpageBarProps) {
  const countForKind = (kind: ManagementKind) => getManagementItems(kind, props.components, props.software).length;
  const visibleKinds = () => managementKinds().filter((kind) =>
    props.mutablePersistenceEnabled || kind.id === "Dependency" || kind.id === "Support");
  return (
    <nav class="page-subnav" aria-label={uiText.management.categoryNav}>
      <For each={visibleKinds()}>
        {(kind) => (
          <button
            id={`managementTab-${kind.id}`}
            class="page-subnav-tab page-tab"
            classList={{ active: props.activeSubpage === kind.id }}
            type="button"
            aria-current={props.activeSubpage === kind.id ? "page" : undefined}
            onClick={() => props.onSubpageChange(kind.id)}
          >
            {kind.label} {countForKind(kind.id)}
          </button>
        )}
      </For>
      <button
        id="managementTab-BrowserRuntime"
        class="page-subnav-tab page-tab"
        classList={{ active: props.activeSubpage === browserRuntimeManagementSubpageId }}
        type="button"
        aria-current={props.activeSubpage === browserRuntimeManagementSubpageId ? "page" : undefined}
        onClick={() => props.onSubpageChange(browserRuntimeManagementSubpageId)}
      >
        {uiText.managementPage.browserRuntimeTab}
      </button>
    </nav>
  );
}

export function ManagementPage(props: ManagementPageProps) {
  const headerDemandId = "management.inventory.header";
  useFrontendVisibilityDemand(headerDemandId, [frontendWorkIds.managementInventory]);
  const [searchQuery, setSearchQuery] = createSignal("");
  const allItems = createMemo(() => getManagementItems(props.activeKind, props.components, props.software));
  const normalizedSearchQuery = createMemo(() => searchQuery().trim());
  const items = createMemo(() => filterManagementItems(allItems(), normalizedSearchQuery()));
  const activeKind = () => {
    const kinds = managementKinds();
    return kinds.find((kind) => kind.id === props.activeKind) ?? kinds[0];
  };
  createEffect(on(() => props.activeKind, () => setSearchQuery(""), { defer: true }));
  return (
    <section
      id="componentsPage"
      class="page active-page"
      aria-label={uiText.managementPage.pageLabel(activeKind().label)}
    >
      <section class="management-surface">
        <div
          {...frontendVisibilitySurface(
            "visible.management.inventory.header.surface",
            [headerDemandId])}
          class="panel-header"
        >
          <h2>{activeKind().label}</h2>
          <div class="panel-header-actions">
            <label class="toolbar-search">
              <input
                type="search"
                placeholder={uiText.managementPage.search}
                aria-label={uiText.managementPage.searchLabel(activeKind().label)}
                value={searchQuery()}
                onInput={(event) => setSearchQuery(event.currentTarget.value)}
              />
            </label>
            <Show when={props.mutablePersistenceEnabled && canAddManualSoftware(props.activeKind)}>
              <button class="panel-refresh-button secondary" type="button" onClick={() => props.onAddManualSoftware(props.activeKind)}>
                {uiText.management.add}
              </button>
            </Show>
            <button class="panel-refresh-button secondary" type="button" disabled={props.refreshInProgress} onClick={props.onRefresh}>
              {props.refreshInProgress ? uiText.management.refreshing : uiText.management.refresh}
            </button>
            <span id="managementCount">
              {observationCanRender(props.observation) ? uiText.managementPage.itemCount(items().length) : "--"}
            </span>
          </div>
        </div>
        <Show when={props.activeKind === "Dependency" || props.activeKind === "Support"}>
          <ObservationStateNotice
            state={props.softwareObservation}
            label={uiText.managementPage.softwareRegistrySupplement}
            profileDisabledMessage={uiText.managementPage.softwareRegistrySupplementDisabled}
          />
        </Show>
        <Show when={props.runtimeEffectsEnabled}>
          <ObservationStateNotice
            state={props.operationsObservation}
            label={uiText.managementPage.operationState}
          />
        </Show>
        <ObservationStateBoundary
          state={props.observation}
          label={props.activeKind === "Dependency" || props.activeKind === "Support"
            ? uiText.managementPage.componentCatalog
            : uiText.managementPage.softwareRegistry}
        >
          <div class="management-list">
            <Show
              when={items().length > 0}
              fallback={
                <div class="management-empty" role="status">
                  <span>
                    {normalizedSearchQuery()
                      ? uiText.management.noSearchResults(normalizedSearchQuery())
                      : uiText.management.emptyCategory}
                  </span>
                  <Show when={normalizedSearchQuery()}>
                    <button class="secondary" type="button" onClick={() => setSearchQuery("")}>
                      {uiText.management.clearSearch}
                    </button>
                  </Show>
                </div>
              }
            >
              <For each={items()}>
                {(item) => item.type === "component"
                  ? <ComponentCard
                      component={item.value as ManagedComponent}
                      actionLabels={props.actionLabels}
                      software={props.software}
                      runtimeEffectsEnabled={props.runtimeEffectsEnabled}
                      onInstall={props.onInstallComponent}
                      onVerify={props.onVerifyComponent}
                      onUninstall={props.onUninstallSoftware}
                      onOpenDetail={(component) => props.onOpenDetail("component", component)}
                    />
                  : <SoftwareCard
                      software={item.value as SoftwareRecord}
                      actionLabels={props.actionLabels}
                      runtimeEffectsEnabled={props.runtimeEffectsEnabled}
                      onUninstall={props.onUninstallSoftware}
                      onClassify={props.onClassifySoftware}
                      mutablePersistenceEnabled={props.mutablePersistenceEnabled}
                      onOpenDetail={(software) => props.onOpenDetail("software", software)}
                      onContextMenu={(event, software) => props.onSoftwareContextMenu(event, softwareContextTargetFromSoftware(software))}
                      onRemoveManual={props.onRemoveManual}
                    />}
              </For>
            </Show>
          </div>
        </ObservationStateBoundary>
      </section>
    </section>
  );
}

function ComponentCard(props: {
  component: ManagedComponent;
  actionLabels: Record<string, string>;
  software: SoftwareRecord[];
  runtimeEffectsEnabled: boolean;
  onInstall: (component: ManagedComponent) => void;
  onVerify: (component: ManagedComponent) => void;
  onUninstall: (software: SoftwareRecord, actionKey?: string) => void;
  onOpenDetail: (component: ManagedComponent) => void;
}) {
  const demandId = frontendVisibilityDemandId(
    "management.component",
    props.component.definition?.id);
  const key = () => managementActionKey("component", props.component.definition?.id);
  const activeLabel = () => props.actionLabels[key()];
  const dependencySoftware = () => findDependencySoftwareForComponent(props.component, props.software);
  return (
    <article
      {...frontendVisibilitySurface(`visible.${demandId}.surface`, [demandId])}
      class="management-card component-item"
    >
      <FrontendVisibilityDemandBinding
        demandId={demandId}
        workIds={[frontendWorkIds.managementInventory]}
      />
      <div class="component-summary">
        <strong class="management-card-title">
          {componentDisplayName(props.component.definition?.id)}
        </strong>
        <div class="management-card-purpose">{componentPurpose(props.component.definition?.id)}</div>
        <Show when={props.component.state === "payload_missing" || props.component.state === "update_available"}>
          <ManagementStatusTag label={userFacingState(props.component.state)} />
          <div class="management-card-purpose">{renderBackendMessage(props.component.message)}</div>
        </Show>
      </div>
      <div class="component-actions">
        <Show
          when={!activeLabel()}
          fallback={<ManagementStatusTag label={activeLabel()} pending />}
        >
          <Show
            when={!componentNeedsUpdate(props.component) && isComponentInstalled(props.component)}
            fallback={
              <button
                class="management-action-button"
                type="button"
                disabled={!props.runtimeEffectsEnabled || !componentCanInstall(props.component)}
                title={props.runtimeEffectsEnabled ? undefined : uiText.managementPage.viewOnly}
                onClick={() => props.onInstall(props.component)}
              >
                {componentInstallLabel(props.component)}
              </button>
            }
          >
            <Show
              when={dependencySoftware()?.operations?.canUninstall}
              fallback={<Show when={props.component.state !== "payload_missing" && props.component.state !== "update_available"}><ManagementStatusTag label={uiText.management.installed} /></Show>}
            >
              <button class="management-action-button" type="button" disabled={!props.runtimeEffectsEnabled} title={props.runtimeEffectsEnabled ? undefined : uiText.managementPage.viewOnly} onClick={() => props.onUninstall(dependencySoftware()!, key())}>
                {uiText.management.uninstall}
              </button>
            </Show>
          </Show>
        </Show>
        <Show when={props.component.canVerify}>
          <button class="management-action-button" type="button"
            disabled={!props.runtimeEffectsEnabled || Boolean(activeLabel())}
            title={props.runtimeEffectsEnabled ? undefined : uiText.managementPage.viewOnly}
            onClick={() => props.onVerify(props.component)}>
            {componentVerifyLabel(props.component.state)}
          </button>
        </Show>
        <DetailsButton
          label={uiText.managementPage.detailsOf(componentDisplayName(props.component.definition?.id))}
          onClick={() => props.onOpenDetail(props.component)}
        />
      </div>
    </article>
  );
}

function SoftwareCard(props: {
  software: SoftwareRecord;
  mutablePersistenceEnabled: boolean;
  onClassify: (software: SoftwareRecord) => void;
  actionLabels: Record<string, string>;
  runtimeEffectsEnabled: boolean;
  onRemoveManual: (software: SoftwareRecord) => void;
  onUninstall: (software: SoftwareRecord, actionKey?: string) => void;
  onOpenDetail: (software: SoftwareRecord) => void;
  onContextMenu: (event: MouseEvent, software: SoftwareRecord) => void;
}) {
  const demandId = frontendVisibilityDemandId("management.software", props.software.id);
  const key = () => managementActionKey("software", props.software.id);
  const activeLabel = () => props.actionLabels[key()];
  const canUninstall = () => Boolean(props.software.operations?.canUninstall) && !activeLabel();
  // 右侧状态标签的文字；能卸载时由"卸载"按钮表达，没有标签。
  const statusLabel = () => activeLabel() || (isUnclassifiedSoftware(props.software) ? uiText.softwareKind.Unclassified : canUninstall() ? "" : uiText.management.installed);
  // 这张卡要不要说话：状态已经由右侧表达，只复述状态的消息一律不显示。
  const cardPurpose = () => {
    if (isUnclassifiedSoftware(props.software)) {
      return "";
    }
    const purpose = softwareSummary(props.software);
    return repeatsStatus(purpose, statusLabel()) ? "" : purpose;
  };

  return (
    <article
      {...frontendVisibilitySurface(`visible.${demandId}.surface`, [demandId])}
      class="management-card software-item"
      onContextMenu={(event) => props.onContextMenu(event, props.software)}
    >
      <FrontendVisibilityDemandBinding
        demandId={demandId}
        workIds={[frontendWorkIds.managementInventory]}
      />
      <div class="software-summary">
        <div class="management-card-title-row">
          <strong class="management-card-title">{softwareRecordName(props.software) || "--"}</strong>
          <Show when={props.software.requiresRootPathConfirmation}>
            <AlertTriangle
              class="management-root-warning-icon"
              size={18}
              aria-label={uiText.managementPage.rootNeedsCheck}
            />
          </Show>
        </div>
        <SoftwareIssueTagStrip issues={props.software.issues} />
        <Show when={isUnclassifiedSoftware(props.software)}>
          <div class="management-card-purpose">{uiText.management.unclassifiedReminder}</div>
          <div class="management-card-purpose">{runtimeDiscoveryLabel(props.software.kind)}</div>
          <For each={props.software.rootPaths ?? []}>
            {(root) => <div class="management-card-purpose">{root}</div>}
          </For>
        </Show>
        {/*
          这一行**只在有话要说时才出现**。状态由右边的标签或按钮表达，
          再用一句话复述"已安装在哪个目录"、"可以从官方下载"只是重复。
        */}
        <Show when={cardPurpose()}>
          <div class="management-card-purpose">{cardPurpose()}</div>
        </Show>
      </div>
      <div class="software-actions">
        <Show when={props.mutablePersistenceEnabled && props.software.registrationFacts?.kind === "manual"}>
          <button class="management-action-button" type="button" disabled={Boolean(activeLabel())} onClick={() => props.onRemoveManual(props.software)}>
            {uiText.managementFeedback.removeManual}
          </button>
        </Show>
        <Show when={props.mutablePersistenceEnabled && isUnclassifiedSoftware(props.software)}>
          <button class="management-action-button" type="button" onClick={() => props.onClassify(props.software)}>
            {uiText.management.classifySoftware}
          </button>
        </Show>
        <Show
          when={canUninstall()}
          fallback={<ManagementStatusTag label={statusLabel()} pending={Boolean(activeLabel())} />}
        >
          <button class="management-action-button" type="button" disabled={!props.runtimeEffectsEnabled} title={props.runtimeEffectsEnabled ? undefined : uiText.managementPage.viewOnly} onClick={() => props.onUninstall(props.software, key())}>
            {uiText.management.uninstall}
          </button>
        </Show>
        <SettingsButton onClick={() => props.onOpenDetail(props.software)} />
        <MoreActionsButton
          label={uiText.managementPage.moreActionsOf(softwareRecordName(props.software) || uiText.managementPage.currentSoftware)}
          focusKey={`management-software-actions:${props.software.id}`}
          onOpen={(event) => props.onContextMenu(event, props.software)}
        />
      </div>
    </article>
  );
}

// 状态只是告知，不是可点的动作，所以用标签而不是禁用按钮。
function ManagementStatusTag(props: { label: string; pending?: boolean }) {
  return (
    <span class="management-status-tag" classList={{ pending: props.pending === true }}>
      <Show when={!props.pending}>
        <Check aria-hidden="true" size={14} strokeWidth={2.5} />
      </Show>
      {props.label}
    </span>
  );
}

// 消息只复述状态（"已安装。"对"已安装"）时算重复。
function repeatsStatus(message: string, status: string) {
  const plain = (text: string) => text.trim().replace(/[。！？.!?]+$/, "").toLocaleLowerCase();
  return Boolean(status) && plain(message) === plain(status);
}

function SettingsButton(props: { onClick: () => void }) {
  return (
    <button class="management-settings-button" type="button" title={uiText.management.softwareSettings} aria-label={uiText.management.softwareSettings} onClick={props.onClick}>
      <Settings aria-hidden="true" size={16} strokeWidth={2} />
    </button>
  );
}

function DetailsButton(props: { label: string; onClick: () => void }) {
  return (
    <button
      class="management-settings-button"
      type="button"
      title={uiText.managementPage.details}
      aria-label={props.label}
      onClick={props.onClick}
    >
      <Info aria-hidden="true" size={16} strokeWidth={2} />
    </button>
  );
}

function MoreActionsButton(props: {
  label: string;
  focusKey: string;
  onOpen: (event: MouseEvent) => void;
}) {
  const open = (element: HTMLElement) => props.onOpen(contextMenuEventForElement(element));
  return (
    <button
      class="management-settings-button"
      type="button"
      title={uiText.managementPage.moreActions}
      aria-label={props.label}
      data-focus-key={props.focusKey}
      onClick={(event) => open(event.currentTarget)}
      onKeyDown={(event) => {
        if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
          event.preventDefault();
          open(event.currentTarget);
        }
      }}
    >
      <MoreHorizontal aria-hidden="true" size={16} strokeWidth={2} />
    </button>
  );
}

function contextMenuEventForElement(element: HTMLElement) {
  const rect = element.getBoundingClientRect();
  return new MouseEvent("contextmenu", {
    clientX: Math.min(window.innerWidth - 8, rect.right),
    clientY: Math.min(window.innerHeight - 8, rect.bottom),
    bubbles: false,
    cancelable: true
  });
}

/**
 * 按钮文案如实反映这次点下去会发生什么：缓存里有安装器就是"安装"，
 * 能自动获取就是"下载并安装"，只能人工获取就是"获取安装器"。
 */
function componentInstallLabel(component: ManagedComponent) {
  if (componentNeedsUpdate(component)) {
    return uiText.componentCatalog.update;
  }
  if (component.installerAvailable) {
    return uiText.management.install;
  }
  if ((component.installerSourceKind ?? "manual") === "manual") {
    return uiText.management.obtainInstaller;
  }
  return uiText.management.downloadAndInstall;
}

function componentCanInstall(component: ManagedComponent) {
  return Boolean(component.canInstall || component.canDownload || component.installerAvailable || component.definition?.sourcePageUrl);
}

function canAddManualSoftware(kind: ManagementKind) {
  return kind === "adapted" || kind === "game" || kind === "high_performance" || kind === "other";
}

function softwareContextTargetFromSoftware(software: SoftwareRecord): SoftwareContextMenuTarget {
  return {
    kind: "software",
    name: softwareRecordName(software) || "--",
    softwareId: software.id,
    softwareName: softwareRecordName(software),
    rootPaths: software.rootPaths ?? []
  };
}

function getManagementItems(kind: ManagementKind, components: ManagedComponent[], software: SoftwareRecord[]): ManagementItem[] {
  if (kind === "Dependency" || kind === "Support") {
    return getManagementRoleItems(kind, components, software);
  }

  if (kind === "Unclassified") {
    return software.filter(isUnclassifiedSoftware).map((item) => ({ type: "software", value: item }));
  }

  if (kind === "Unconfirmed") {
    return software
      .filter(isUnconfirmedSoftware)
      .map((item) => ({ type: "software", value: item }));
  }

  return software
    .filter((item) => !isUnconfirmedSoftware(item) && !isUnclassifiedSoftware(item) && normalizeSoftwareKind(item) === kind)
    .map((item) => ({ type: "software", value: item }));
}

function isUnconfirmedSoftware(software: SoftwareRecord) {
  return software.identityConfirmed === false || software.requiresRootPathConfirmation === true;
}

function getManagementRoleItems(
  role: "Dependency" | "Support",
  components: ManagedComponent[],
  software: SoftwareRecord[]
): ManagementItem[] {
  const wireRole: ManagementRole = role === "Dependency" ? "dependency" : "support";
  const roleComponents = components
    .filter((component) => component.definition?.managementRole === wireRole);
  const componentItems = roleComponents
    .filter((component) => component.definition?.id !== sharedBrowserRuntimeComponentId)
    .map((component) => ({ type: "component" as const, value: component }));
  // 共享 WebView2 运行时只在「运行时管理」里列；它在软件清单里的同名记录也要一起去掉，
  // 否则会以后端原名的软件卡片回到依赖列表。
  const componentIds = new Set(roleComponents.map((component) => component.definition?.id));
  const componentNameSet = new Set(roleComponents
    .map((component) => normalizeName(componentDisplayName(component.definition?.id)))
    .filter(Boolean));
  const extraSoftwareItems = software
    .filter((item) => !isUnconfirmedSoftware(item) && item.managementRole === wireRole)
    .filter((item) => !componentIds.has(item.id.replace(/^dependency:/, "")) && !componentNameSet.has(normalizeName(item.name)))
    .map((item) => ({ type: "software" as const, value: item }));
  return [...componentItems, ...extraSoftwareItems];
}

function filterManagementItems(items: ManagementItem[], query: string) {
  const normalizedQuery = normalizeSearchQuery(query);
  if (!normalizedQuery) {
    return items;
  }

  return items.filter((item) => item.type === "component"
    ? matchesComponentSearch(item.value as ManagedComponent, normalizedQuery)
    : matchesSoftwareSearch(item.value as SoftwareRecord, normalizedQuery));
}

function matchesComponentSearch(component: ManagedComponent, query: string) {
  const definition = component.definition ?? {};
  return matchesSearch(query,
    definition.id,
    componentDisplayName(definition.id),
    componentPurpose(definition.id),
    definition.vendor,
    definition.managementRole,
    definition.sourcePageUrl,
    componentInstallNote(definition.id),
    component.state,
    renderBackendMessage(component.message),
    component.installRoot,
    component.installerDirectory,
    component.installerPath,
    ...(component.providers ?? []).flatMap((provider) => [
      provider.providerId,
      provider.state,
      provider.providerKind,
      renderBackendMessage(provider.message),
      ...(provider.capabilities ?? []).map(componentCapabilityLabel)
    ]));
}

function matchesSoftwareSearch(software: SoftwareRecord, query: string) {
  return matchesSearch(query,
    software.id,
    softwareRecordName(software),
    software.kind,
    software.managementRole,
    softwareDisplayKindLabel(software.kind),
    software.state,
    softwareSummary(software),
    ...(software.issues ?? []).flatMap((issue) => [
      issue.kind,
      softwareIssueLabel(issue),
      softwareIssueMessage(issue),
      issue.source,
      ...(issue.references ?? []).flatMap((reference) => [reference.label, reference.url])
    ]),
    ...softwareSourceLabels(software),
    ...(software.rootPaths ?? []),
    software.operations?.uninstallKind,
    softwareUninstallDescription(software));
}

function normalizeSearchQuery(value: string) {
  return value.trim().toLocaleLowerCase();
}

function matchesSearch(query: string, ...values: unknown[]) {
  return values.some((value) => String(value ?? "").toLocaleLowerCase().includes(query));
}

export function managementActionKey(type: string, id: unknown) {
  return `${type}:${String(id ?? "").toLowerCase()}`;
}

export function managementActionKeyFromOperation(operation: OperationSnapshot) {
  const targetId = operation.subject?.targetId;
  if (operation.kind === "component.install" && targetId) {
    return managementActionKey("component", targetId);
  }
  if (operation.kind === "software.uninstall" && targetId) {
    return managementActionKey("software", targetId);
  }

  return null;
}

export function findDependencySoftwareForComponent(component: ManagedComponent, software: SoftwareRecord[]) {
  if (component.definition?.managementRole !== "dependency") {
    return undefined;
  }
  const componentName = normalizeName(componentDisplayName(component.definition?.id));
  const componentId = normalizeName(component.definition?.id);
  return software.find((item) =>
    item.managementRole === "dependency"
    && (normalizeName(item.name) === componentName || normalizeName(item.id).includes(componentId)));
}
