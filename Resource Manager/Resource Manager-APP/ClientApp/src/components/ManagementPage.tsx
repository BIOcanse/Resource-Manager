import { createEffect, createMemo, createSignal, For, on, Show } from "solid-js";
import { AlertTriangle, Info, MoreHorizontal, Settings } from "lucide-solid";
import { sharedBrowserRuntimeComponentId } from "../browserRuntimes/browserRuntimeTypes";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import {
  frontendVisibilityDemandId,
  frontendVisibilitySurface
} from "../frontendWork/frontendVisibilitySurface";
import {
  FrontendVisibilityDemandBinding,
  useFrontendVisibilityDemand
} from "../frontendWork/useFrontendVisibilityDemand";
import { componentDisplayName, componentPurpose, userFacingMessage } from "../presentation/userFacingText";
import type { SoftwareContextMenuTarget } from "./SoftwareContextMenu";
import {
  browserRuntimeManagementSubpageId,
  managementInventoryKinds,
  migrationManagementSubpageId,
  type ManagementSubpageId
} from "../management/managementNavigation";
import type { ManagedComponent, ManagementKind, OperationSnapshot, SoftwareRecord } from "../types";
import { managementKindLabel, softwareDisplayKindLabel, uiText } from "../text.ts";
import { isComponentInstalled, normalizeName, normalizeSoftwareKind } from "../utils";
import {
  observationCanRender,
  type ObservationState
} from "../observation/observationState";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "./ObservationStateNotice";
import { SoftwareIssueTagStrip } from "./SoftwareIssuePresentation";

export const managementKinds: Array<{ id: ManagementKind; label: string }> = managementInventoryKinds.map((id) => ({
  id,
  label: managementKindLabel(id)
}));

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
  onRefresh: () => void;
  onInstallComponent: (component: ManagedComponent) => void;
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
  const visibleKinds = () => managementKinds.filter((kind) =>
    props.mutablePersistenceEnabled || kind.id === "Dependency" || kind.id === "Support");
  return (
    <nav class="management-tabs" aria-label={uiText.management.categoryNav}>
      <For each={visibleKinds()}>
        {(kind) => (
          <button
            id={`managementTab-${kind.id}`}
            class="management-tab"
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
        class="management-tab"
        classList={{ active: props.activeSubpage === browserRuntimeManagementSubpageId }}
        type="button"
        aria-current={props.activeSubpage === browserRuntimeManagementSubpageId ? "page" : undefined}
        onClick={() => props.onSubpageChange(browserRuntimeManagementSubpageId)}
      >
        运行时管理
      </button>
      <Show when={props.runtimeEffectsEnabled}>
        <button
          id="managementTab-Migration"
          class="management-tab"
          classList={{ active: props.activeSubpage === migrationManagementSubpageId }}
          type="button"
          aria-current={props.activeSubpage === migrationManagementSubpageId ? "page" : undefined}
          onClick={() => props.onSubpageChange(migrationManagementSubpageId)}
        >
          迁移工作台
        </button>
      </Show>
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
  const activeKind = () => managementKinds.find((kind) => kind.id === props.activeKind) ?? managementKinds[0];
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
                      runtimeEffectsEnabled={props.runtimeEffectsEnabled}
                      onInstall={props.onInstallComponent}
                      onOpenDetail={(component) => props.onOpenDetail("component", component)}
                    />
                  : <SoftwareCard
                      software={item.value as SoftwareRecord}
                      actionLabels={props.actionLabels}
                      runtimeEffectsEnabled={props.runtimeEffectsEnabled}
                      onUninstall={props.onUninstallSoftware}
                      onOpenDetail={(software) => props.onOpenDetail("software", software)}
                      onContextMenu={(event, software) => props.onSoftwareContextMenu(event, softwareContextTargetFromSoftware(software))}
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
  runtimeEffectsEnabled: boolean;
  onInstall: (component: ManagedComponent) => void;
  onOpenDetail: (component: ManagedComponent) => void;
}) {
  const demandId = frontendVisibilityDemandId(
    "management.component",
    props.component.definition?.id ?? props.component.definition?.name);
  const key = () => managementActionKey("component", props.component.definition?.id);
  const activeLabel = () => props.actionLabels[key()];
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
          {componentDisplayName(props.component.definition?.id, props.component.definition?.name)}
        </strong>
        <div class="management-card-purpose">{componentPurpose(props.component.definition?.id)}</div>
      </div>
      <div class="component-actions">
        <Show
          when={!activeLabel()}
          fallback={<button class="management-action-button" type="button" disabled>{activeLabel()}</button>}
        >
          <Show
            when={isComponentInstalled(props.component)}
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
            {/* 组件只装不卸：已安装的组件不提供卸载入口。 */}
            <button class="management-action-button" type="button" disabled>{uiText.management.installed}</button>
          </Show>
        </Show>
        <DetailsButton
          label={uiText.managementPage.detailsOf(componentDisplayName(props.component.definition?.id, props.component.definition?.name))}
          onClick={() => props.onOpenDetail(props.component)}
        />
      </div>
    </article>
  );
}

function SoftwareCard(props: {
  software: SoftwareRecord;
  actionLabels: Record<string, string>;
  runtimeEffectsEnabled: boolean;
  onUninstall: (software: SoftwareRecord, actionKey?: string) => void;
  onOpenDetail: (software: SoftwareRecord) => void;
  onContextMenu: (event: MouseEvent, software: SoftwareRecord) => void;
}) {
  const demandId = frontendVisibilityDemandId("management.software", props.software.id);
  const key = () => managementActionKey("software", props.software.id);
  const activeLabel = () => props.actionLabels[key()];
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
          <strong class="management-card-title">{props.software.name ?? "--"}</strong>
          <Show when={props.software.requiresRootPathConfirmation}>
            <AlertTriangle
              class="management-root-warning-icon"
              size={18}
              aria-label={uiText.managementPage.rootNeedsCheck}
            />
          </Show>
        </div>
        <SoftwareIssueTagStrip issues={props.software.issues} />
        <div class="management-card-purpose">
          {userFacingMessage(
            props.software.message,
            softwareDisplayKindLabel(props.software.kind, props.software.displayKind))}
        </div>
      </div>
      <div class="software-actions">
        <Show
          when={!activeLabel()}
          fallback={<button class="management-action-button" type="button" disabled>{activeLabel()}</button>}
        >
          <Show
            when={props.software.operations?.canUninstall}
            fallback={<button class="management-action-button" type="button" disabled>{uiText.management.installed}</button>}
          >
            <button class="management-action-button" type="button" disabled={!props.runtimeEffectsEnabled} title={props.runtimeEffectsEnabled ? undefined : uiText.managementPage.viewOnly} onClick={() => props.onUninstall(props.software, key())}>
              {uiText.management.uninstall}
            </button>
          </Show>
        </Show>
        <SettingsButton onClick={() => props.onOpenDetail(props.software)} />
        <MoreActionsButton
          label={uiText.managementPage.moreActionsOf(props.software.name ?? uiText.managementPage.currentSoftware)}
          focusKey={`management-software-actions:${props.software.id}`}
          onOpen={(event) => props.onContextMenu(event, props.software)}
        />
      </div>
    </article>
  );
}

function SettingsButton(props: { onClick: () => void }) {
  return (
    <button class="management-settings-button" type="button" title={uiText.management.settingsAndMigration} aria-label={uiText.management.settingsAndMigration} onClick={props.onClick}>
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
  return kind === "Adapted" || kind === "Game" || kind === "HighPerformance" || kind === "Other";
}

function softwareContextTargetFromSoftware(software: SoftwareRecord): SoftwareContextMenuTarget {
  return {
    name: software.name ?? "--",
    softwareId: software.id,
    softwareName: software.name,
    rootPaths: software.rootPaths ?? []
  };
}

function getManagementItems(kind: ManagementKind, components: ManagedComponent[], software: SoftwareRecord[]): ManagementItem[] {
  if (kind === "Dependency" || kind === "Support") {
    return getManagementRoleItems(kind, components, software);
  }

  if (kind === "Unconfirmed") {
    return software
      .filter(isUnconfirmedSoftware)
      .map((item) => ({ type: "software", value: item }));
  }

  return software
    .filter((item) => !isUnconfirmedSoftware(item) && normalizeSoftwareKind(item) === kind)
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
  const componentItems = components
    .filter((component) => component.definition?.managementRole === role)
    .filter((component) => component.definition?.id !== sharedBrowserRuntimeComponentId)
    .map((component) => ({ type: "component" as const, value: component }));
  const componentNameSet = new Set(componentItems
    .map((item) => normalizeName(item.value.definition?.name))
    .filter(Boolean));
  const extraSoftwareItems = software
    .filter((item) => !isUnconfirmedSoftware(item) && item.managementRole === role)
    .filter((item) => !componentNameSet.has(normalizeName(item.name)))
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
    definition.name,
    definition.purpose,
    definition.vendor,
    definition.category,
    definition.managementRole,
    definition.sourcePageUrl,
    definition.installNote,
    component.state,
    component.stateLabel,
    component.message,
    component.installRoot,
    component.installerDirectory,
    component.installerPath,
    ...(component.providers ?? []).flatMap((provider) => [
      provider.providerId,
      provider.name,
      provider.state,
      provider.providerKind,
      provider.message,
      ...(provider.capabilities ?? [])
    ]));
}

function matchesSoftwareSearch(software: SoftwareRecord, query: string) {
  return matchesSearch(query,
    software.id,
    software.name,
    software.kind,
    software.displayKind,
    software.managementRole,
    softwareDisplayKindLabel(software.kind, software.displayKind),
    software.state,
    software.message,
    ...(software.issues ?? []).flatMap((issue) => [
      issue.kind,
      issue.label,
      issue.message,
      issue.source,
      ...(issue.references ?? []).flatMap((reference) => [reference.label, reference.url])
    ]),
    ...(software.sources ?? []),
    ...(software.rootPaths ?? []),
    software.operations?.uninstallKind,
    software.operations?.uninstallLabel,
    software.operations?.uninstallMessage);
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
  const domainId = operation.domainKey?.split(":").slice(1).join(":");
  if (operation.kind === "component.install" && domainId) {
    return managementActionKey("component", domainId);
  }
  if (operation.kind === "software.uninstall" && domainId) {
    return managementActionKey("software", domainId);
  }

  return null;
}

