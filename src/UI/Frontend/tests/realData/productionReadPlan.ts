// typescript-api is the 6.x compiler under an alias: TypeScript 7.x ships no JavaScript compiler API (its package exports
// only the version). Remove the alias when 7.x ships a stable compiler API: port this file to it and drop typescript-api.
import ts from "typescript-api";
import { readFile, readdir } from "node:fs/promises";
import { dirname, extname, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import type { MetricDefinition, ResourceTableColumnSettings } from "../../src/types.ts";
import type { MetricFieldQuery } from "../../src/data/metricFields/metricFieldSource.ts";
import type { MetricSnapshotQuery } from "../../src/data/monitor/monitorSourcesApi.ts";
import type { GpuSpecializedTelemetryQuery } from "../../src/data/gpu/gpuSchedulingModelSource.ts";
import type { ResourceBarQuery, ResourceBreakdownQuery, ResourceMonitorQuery } from "../../src/data/resourceMonitor/resourceMonitorApi.ts";

export interface Catalogs {
  metrics?: readonly (Pick<MetricDefinition, "id"> & Partial<MetricDefinition>)[];
  // The recorder retains these raw payloads; selector derivation does not read them.
  metricFields?: unknown;
  processFields?: unknown;
}

export interface ObservationReadPlan {
  reads: string[];
  subscriptions: string[];
}

export interface ProductionReadPlan extends ObservationReadPlan {
  streamPath: string;
  discoveryPath: string;
  topologyPath: string | undefined;
  catalogReads: { metrics?: string; processFields?: string };
  categories: { observation: ObservationReadPlan; journey: ObservationReadPlan };
}

type ResourceMonitorModule = typeof import("../../src/data/resourceMonitor/resourceMonitorApi.ts");
type MetricSnapshotModule = typeof import("../../src/data/monitor/monitorSourcesApi.ts");
type MetricFieldModule = typeof import("../../src/data/metricFields/metricFieldSource.ts");
type GpuTelemetryModule = typeof import("../../src/data/gpu/gpuSchedulingModelSource.ts");
type ProductionModule = Record<string, unknown> & Partial<
  ResourceMonitorModule & MetricSnapshotModule & MetricFieldModule & GpuTelemetryModule>;
type BuilderQuery = ResourceMonitorQuery | ResourceBreakdownQuery | MetricSnapshotQuery
  | MetricFieldQuery | GpuSpecializedTelemetryQuery | undefined;
type BuilderArgument = number | BuilderQuery;
// Discovery deliberately follows runtime export names rather than a second
// builder registry. Query shapes below come from their production owners; this
// callable is the boundary for the argument list inspected from each export's AST.
type DiscoveredSubscriptionBuilder = (...args: BuilderArgument[]) => unknown;
interface ProductionSource {
  path: string;
  source: ts.SourceFile;
  module: ProductionModule | null;
}
interface ProductionSources {
  readSources: ProductionSource[];
  builderSources: ProductionSource[];
}
interface ResourceQuerySelectors {
  metricIds: string[];
  fieldQueries: MetricFieldQuery[];
  processDetailLevel: "full" | "basic";
  tableItems: string[];
  bars: ResourceBarQuery[];
  residualDetailMetricIds: string[];
}
type ResourceSelectorModules = [
  typeof import("../../src/preferences/dashboardPreferences.ts"),
  typeof import("../../src/features/resourceTable/resourceTableSources.ts"),
  typeof import("../../src/features/resourceBreakdown/resourceBarSources.ts"),
  typeof import("../../src/stores/monitorConfig.ts"),
  typeof import("../../src/features/monitor/fieldCards.ts"),
  typeof import("../../src/features/details/cpuMetricFields.ts"),
  typeof import("../../src/features/deviceTopology/deviceMetricFields.ts")
];

const sourceRoot = fileURLToPath(new URL("../../src/", import.meta.url));
const ownerPath = resolve(sourceRoot, "frontendRuntime/source/FrontendSources.ts");
const subscriptionChannelPath = resolve(sourceRoot, "frontendRuntime/push/BackendSubscriptionChannel.ts");
const dashboardPreferencesPath = resolve(sourceRoot, "preferences/dashboardPreferences.ts");
const resourceTableSourcesPath = resolve(sourceRoot, "features/resourceTable/resourceTableSources.ts");
const resourceBarSourcesPath = resolve(sourceRoot, "features/resourceBreakdown/resourceBarSources.ts");
const monitorConfigPath = resolve(sourceRoot, "stores/monitorConfig.ts");
const fieldCardsPath = resolve(sourceRoot, "features/monitor/fieldCards.ts");
const cpuFieldsPath = resolve(sourceRoot, "features/details/cpuMetricFields.ts");
const deviceFieldsPath = resolve(sourceRoot, "features/deviceTopology/deviceMetricFields.ts");
const deniedPath = /credentials?|access-keys?|access-requests?|draft|refresh|reapply|secrets?|tokens?/i;
const resourceBreakdownQuery: ResourceBreakdownQuery = {
  bars: null,
  processDetailSoftwareIds: [],
  residualDetailMetricIds: []
};

let sourceGraphPromise: Promise<ProductionSources> | undefined;
let resourceSelectorSourcesPromise: Promise<ResourceSelectorModules> | undefined;

/**
 * Return only production read descriptors and subscription URLs reachable from
 * FrontendSources. Catalog-backed selectors are expanded by the same production
 * builders once the recorder has fetched those catalogs.
 */
export async function deriveProductionReadPlan(intervalMs = 1000, catalogs: Catalogs | null = null): Promise<ProductionReadPlan> {
  if (!Number.isFinite(intervalMs) || intervalMs < 1) {
    throw new Error("Subscription interval must be a positive finite number");
  }
  const sources = await (sourceGraphPromise ??= discoverProductionSources());
  const reads = new Set<string>();
  const subscriptions = new Set<string>();
  const catalogReads: ProductionReadPlan["catalogReads"] = {};
  const observationReads = new Set<string>(), observationSubscriptions = new Set<string>();
  const resourceSelectors = catalogs ? await deriveResourceQuerySelectors(catalogs) : null;
  let discoveryPath: string | null = null;

  for (const { source, path } of sources.readSources) {
    for (const descriptor of findGetDescriptors(source)) {
      if (!isSafeReadPath(descriptor.url) || deniedPath.test(descriptor.key ?? "")) continue;
      reads.add(descriptor.url);
      if (isObservationSource(path, descriptor.key)) observationReads.add(descriptor.url);
      if (descriptor.key === "monitor.metric-catalog") catalogReads.metrics = descriptor.url;
      if (descriptor.key === "resource-breakdown.process-field-catalog") catalogReads.processFields = descriptor.url;
    }
  }

  for (const { source, module, path } of sources.builderSources) {
    for (const builder of findExportedSubscriptionBuilders(source)) {
      if (/AccessRequest/i.test(builder.name)) continue;
      const variants = getBuilderVariants(builder.name, catalogs, module, resourceSelectors);
      if (!variants.length) continue;
      for (const query of variants) {
        const args = builder.parameters.map(parameter => {
          const parameterName = parameterNameOf(parameter);
          if (parameterName === "intervalMs") return intervalMs;
          if (parameterName === "query") return normalizeBuilderQuery(builder.name, query, module);
          if (parameter.questionToken || parameter.initializer) return undefined;
          throw new Error(`Recorder needs a production selector for ${builder.name}.${parameterName}`);
        });
        const pathValue = (module![builder.name] as DiscoveredSubscriptionBuilder)(...args);
        if (typeof pathValue !== "string" || !isSafeSubscriptionPath(pathValue)) continue;
        subscriptions.add(pathValue);
        if (isObservationSource(path, builder.name)) observationSubscriptions.add(pathValue);
        if (builder.name === "buildMetricFieldCatalogSubscriptionUrl") discoveryPath = pathValue;
      }
    }
  }

  const streamPath = await readSubscriptionEndpoint();
  // Some GET descriptors call a pure URL builder. Their literal endpoint is
  // discovered elsewhere (for example the legacy API facade), but its owner is
  // the measurement builder, not that facade.
  for (const { path, source } of sources.builderSources) {
    if (!isObservationSource(path)) continue;
    for (const statement of source.statements) {
      if (!ts.isFunctionDeclaration(statement) || !statement.name || !/^build.*Url$/.test(statement.name.text)
        || /Subscription/.test(statement.name.text)) continue;
      const pending: ts.Node[] = [statement];
      while (pending.length) {
        const node = pending.pop()!;
        const literal = literalString(node);
        if (literal && reads.has(literal)) observationReads.add(literal);
        node.forEachChild(child => { pending.push(child); });
      }
    }
  }
  if (!reads.size || !subscriptions.size || !discoveryPath) {
    throw new Error("Production read plan is incomplete; expected GETs, safe subscription builders, and a metric catalog stream");
  }
  return {
    reads: [...reads].sort(),
    subscriptions: [...subscriptions].sort(),
    streamPath,
    discoveryPath,
    topologyPath: [...subscriptions].find(path => path.startsWith("/api/device-topology/state/subscribe")),
    catalogReads,
    categories: { observation: { reads: [...observationReads].sort(), subscriptions: [...observationSubscriptions].sort() },
      journey: { reads: [...reads].filter(path => !observationReads.has(path)).sort(),
        subscriptions: [...subscriptions].filter(path => !observationSubscriptions.has(path)).sort() } }
  };
}

// Classify the production owners, never a second list of HTTP endpoints. Settings,
// control, management, operations, updates, recordings and audit default to journey.
function isObservationSource(path: string, key: string | null = ""): boolean {
  const module = relative(sourceRoot, path).split(sep).join("/");
  return /^(data\/(monitor|metricFields|resourceMonitor|cpu|gpu|localSystem)\/|features\/deviceTopology\/)/.test(module)
    && !/override|score|binding|settings|control/i.test(key ?? "");
}

export function assertAllowedRequest(plan: Pick<ProductionReadPlan, "reads" | "subscriptions" | "streamPath">,
  method: string, path: string, body: unknown): void {
  if (method === "GET" && plan.reads.includes(path) && body === undefined) return;
  const expected = {
    version: 1,
    subscriptions: plan.subscriptions.map((subscriptionPath, index) => ({ id: `slice:${index}`, path: subscriptionPath }))
  };
  if (method === "POST" && path === plan.streamPath && JSON.stringify(body) === JSON.stringify(expected)) return;
  throw new Error(`Recorder refused ${method} ${path}`);
}

async function discoverProductionSources(): Promise<ProductionSources> {
  const queue = [ownerPath];
  const visited = new Set<string>();
  const sources: ProductionSource[] = [];
  while (queue.length) {
    if (visited.size >= 1000) throw new Error("Production API module limit exceeded");
    const path = queue.pop()!;
    if (visited.has(path)) continue;
    visited.add(path);
    const text = await readFile(path, "utf8");
    const source = ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true);
    const isOwner = path === ownerPath;
    if (isOwner || isRelevantDataModule(path)) {
      const module: ProductionModule | null = isOwner ? null : await import(pathToFileURL(path).href) as ProductionModule;
      sources.push({ path, source, module });
    }
    for (const statement of source.statements) {
      if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) continue;
      const specifier = statement.moduleSpecifier.text;
      if (!specifier.startsWith(".")) continue;
      const imported = resolveImportedTypeScript(dirname(path), specifier);
      if (imported && isRelevantDataModule(imported) && !visited.has(imported)) queue.push(imported);
    }
  }
  const readPaths = await findTypeScriptFiles(sourceRoot);
  const graphSources = new Map(sources.map(item => [item.path, item]));
  const readSources: ProductionSource[] = [];
  for (const path of readPaths) {
    const existing = graphSources.get(path);
    if (existing) {
      readSources.push(existing);
      continue;
    }
    const text = await readFile(path, "utf8");
    readSources.push({ path, source: ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true), module: null });
  }
  return {
    readSources,
    builderSources: sources.sort((left, right) => left.path.localeCompare(right.path))
  };
}

async function findTypeScriptFiles(root: string): Promise<string[]> {
  const pending = [root];
  const files: string[] = [];
  let visitedDirectories = 0;
  while (pending.length) {
    if (++visitedDirectories > 1000 || files.length > 5000) throw new Error("Frontend source file limit exceeded");
    const directory = pending.pop()!;
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) pending.push(path);
      else if (entry.isFile() && (entry.name.endsWith(".ts") || entry.name.endsWith(".tsx"))) files.push(path);
    }
  }
  return files.sort();
}

function resolveImportedTypeScript(directory: string, specifier: string): string | null {
  const target = resolve(directory, specifier);
  if (extname(target)) return target.endsWith(".ts") ? target : null;
  return `${target}.ts`;
}

function isRelevantDataModule(path: string): boolean {
  const relativePath = relative(sourceRoot, path);
  if (!relativePath || relativePath === ".." || relativePath.startsWith(`..${sep}`)) return false;
  const firstSegment = relativePath.split(sep, 1)[0];
  return (firstSegment === "data" || firstSegment === "features") && path.endsWith(".ts");
}

function findGetDescriptors(source: ts.SourceFile): { url: string; key: string | null }[] {
  const result: { url: string; key: string | null }[] = [];
  const pending: ts.Node[] = [...source.statements];
  let visitedNodes = 0;
  while (pending.length) {
    if (++visitedNodes > 200_000) throw new Error("Production API syntax tree limit exceeded");
    const node = pending.pop()!;
    if (ts.isObjectLiteralExpression(node)) {
      const properties = new Map<string, ts.Expression>();
      for (const property of node.properties) {
        if (!ts.isPropertyAssignment(property)) continue;
        const name = propertyNameOf(property.name);
        if (name) properties.set(name, property.initializer);
      }
      const key = literalString(properties.get("key"));
      const request = properties.get("request");
      if (isRequestClientDescriptor(node) && isDefaultOrExplicitGet(request)) {
        for (const url of literalUrlAlternatives(properties.get("url"))) result.push({ url, key });
      }
    }
    if (ts.isCallExpression(node)) {
      const name = callNameOf(node.expression);
      if (name === "getJson") {
        for (const url of literalUrlAlternatives(node.arguments[0])) result.push({ url, key: null });
      } else if (name === "requestJson" && isExplicitOrDefaultGet(node.arguments[1])) {
        for (const url of literalUrlAlternatives(node.arguments[0])) result.push({ url, key: null });
      }
    }
    node.forEachChild(child => { pending.push(child); });
  }
  return result;
}

function isRequestClientDescriptor(node: ts.ObjectLiteralExpression): boolean {
  const call = node.parent;
  return ts.isCallExpression(call) && call.arguments[0] === node
    && ts.isPropertyAccessExpression(call.expression) && call.expression.name.text === "request";
}

function isDefaultOrExplicitGet(request: ts.Expression | undefined): boolean {
  if (!request) return true;
  if (!ts.isObjectLiteralExpression(request) || request.properties.some(property => ts.isSpreadAssignment(property)
    || (property.name && ts.isComputedPropertyName(property.name)))) return false;
  const method = request.properties.find(property => property.name && propertyNameOf(property.name) === "method");
  if (method) return ts.isPropertyAssignment(method) && literalString(method.initializer) === "GET";
  return !request.properties.some(property => property.name && propertyNameOf(property.name) === "body");
}

function literalUrlAlternatives(node: ts.Expression | undefined): string[] {
  if (!node) return [];
  const valuesByNode = new Map<ts.Expression, string[]>();
  const pending = [{ node, visited: false }];
  let visitedNodes = 0;
  while (pending.length) {
    if (++visitedNodes > 100_000) throw new Error("Production URL expression limit exceeded");
    const current = pending.pop()!;
    const expression = current.node;
    if (!current.visited) {
      const literal = literalString(expression);
      if (literal !== null) {
        valuesByNode.set(expression, [literal]);
        continue;
      }
      const children = ts.isParenthesizedExpression(expression) || ts.isAsExpression(expression)
        || ts.isTypeAssertionExpression(expression) || ts.isNonNullExpression(expression)
        ? [expression.expression]
        : ts.isConditionalExpression(expression) ? [expression.whenTrue, expression.whenFalse]
          : ts.isBinaryExpression(expression) && expression.operatorToken.kind === ts.SyntaxKind.PlusToken
            ? [expression.left, expression.right] : [];
      if (!children.length) {
        valuesByNode.set(expression, []);
        continue;
      }
      pending.push({ node: expression, visited: true });
      for (let index = children.length - 1; index >= 0; index -= 1) {
        pending.push({ node: children[index], visited: false });
      }
      continue;
    }

    if (ts.isParenthesizedExpression(expression) || ts.isAsExpression(expression)
      || ts.isTypeAssertionExpression(expression) || ts.isNonNullExpression(expression)) {
      valuesByNode.set(expression, valuesByNode.get(expression.expression) ?? []);
    } else if (ts.isConditionalExpression(expression)) {
      valuesByNode.set(expression, uniqueUrlValues([
        ...(valuesByNode.get(expression.whenTrue) ?? []),
        ...(valuesByNode.get(expression.whenFalse) ?? [])
      ]));
    } else if (ts.isBinaryExpression(expression) && expression.operatorToken.kind === ts.SyntaxKind.PlusToken) {
      const left = valuesByNode.get(expression.left) ?? [];
      const right = valuesByNode.get(expression.right) ?? [];
      if (left.length * right.length > 32) throw new Error("Production URL variant limit exceeded");
      valuesByNode.set(expression, uniqueUrlValues(left.flatMap(prefix => right.map(suffix => prefix + suffix))));
    }
  }
  return valuesByNode.get(node) ?? [];
}

function uniqueUrlValues(values: readonly string[]): string[] {
  return [...new Set(values)];
}

function callNameOf(expression: ts.Expression): string {
  if (ts.isIdentifier(expression)) return expression.text;
  return ts.isPropertyAccessExpression(expression) ? expression.name.text : "";
}

function isExplicitOrDefaultGet(options: ts.Expression | undefined): boolean {
  if (!options) return true;
  if (!ts.isObjectLiteralExpression(options) || options.properties.some(property => ts.isSpreadAssignment(property)
    || (property.name && ts.isComputedPropertyName(property.name)))) return false;
  if (options.properties.some(property => {
    if (!property.name || propertyNameOf(property.name) !== "binary") return false;
    return !ts.isPropertyAssignment(property) || property.initializer.kind !== ts.SyntaxKind.FalseKeyword;
  })) return false;
  const method = options.properties.find(property => property.name && propertyNameOf(property.name) === "method");
  if (method) return ts.isPropertyAssignment(method) && literalString(method.initializer) === "GET";
  return !options.properties.some(property => property.name && propertyNameOf(property.name) === "body");
}

function findExportedSubscriptionBuilders(source: ts.SourceFile): { name: string; parameters: ts.NodeArray<ts.ParameterDeclaration> }[] {
  const builders: { name: string; parameters: ts.NodeArray<ts.ParameterDeclaration> }[] = [];
  for (const statement of source.statements) {
    if (ts.isFunctionDeclaration(statement)
      && hasExportModifier(statement)
      && /^build.*SubscriptionUrl$/.test(statement.name?.text ?? "")) {
      builders.push({ name: statement.name!.text, parameters: statement.parameters });
      continue;
    }
    if (!ts.isVariableStatement(statement) || !hasExportModifier(statement)) continue;
    for (const declaration of statement.declarationList.declarations) {
      if (ts.isIdentifier(declaration.name)
        && /^build.*SubscriptionUrl$/.test(declaration.name.text)
        && declaration.initializer
        && (ts.isArrowFunction(declaration.initializer) || ts.isFunctionExpression(declaration.initializer))) {
        builders.push({ name: declaration.name.text, parameters: declaration.initializer.parameters });
      }
    }
  }
  return builders;
}

function getBuilderVariants(name: string, catalogs: Catalogs | null, module: ProductionModule | null,
  resourceSelectors: ResourceQuerySelectors | null): BuilderQuery[] {
  if (name === "buildResourceMonitorSubscriptionUrl") {
    if (!resourceSelectors) return [];
    return ([
      {
        ...resourceBreakdownQuery,
        scope: "table",
        processDetailLevel: resourceSelectors.processDetailLevel,
        sampleMetricIds: resourceSelectors.tableItems,
        visibleColumnIds: [],
        sortColumnId: "",
        sortDirection: "desc",
        tableMode: "software"
      },
      {
        ...resourceBreakdownQuery,
        bars: resourceSelectors.bars,
        scope: "bars",
        sampleMetricIds: null,
        residualDetailMetricIds: resourceSelectors.residualDetailMetricIds,
        visibleColumnIds: [],
        sortColumnId: "",
        sortDirection: "desc",
        tableMode: "software"
      }
    ] satisfies ResourceMonitorQuery[]).filter(query => query.scope === "table" ? resourceSelectors.tableItems.length > 0 : resourceSelectors.bars.length > 0);
  }
  if (name === "buildMetricSnapshotSubscriptionUrl") {
    if (!resourceSelectors) return [];
    const ids = resourceSelectors.metricIds;
    return ids.length ? [{ ids }] : [];
  }
  if (name === "buildMetricFieldSubscriptionUrl") {
    return resourceSelectors?.fieldQueries ?? [];
  }
  if (name === "buildGpuSpecializedTelemetrySubscriptionUrl") {
    if (!catalogs) return [];
    const indexes = [...new Set((catalogs.metrics ?? []).flatMap(item => {
      const match = typeof item?.id === "string" ? /^gpu\.(\d+)\./.exec(item.id) : null;
      return match ? [Number(match[1])] : [];
    }))].filter(Number.isSafeInteger).sort((left, right) => left - right);
    const counterIds = typeof module!.buildGpuSpecializedCounterIds === "function"
      ? uniqueStrings(module!.buildGpuSpecializedCounterIds(indexes))
      : [];
    return counterIds.length ? [{ counterIds }] : [];
  }
  if (name === "buildResourceProcessDetailsSubscriptionUrl") {
    // Process detail selectors require software IDs from a live resource facts
    // response. The full row pool is captured by resource facts instead.
    return [];
  }
  return [undefined];
}

function normalizeBuilderQuery(name: string, query: BuilderQuery, module: ProductionModule | null): BuilderQuery {
  const normalizerName = name === "buildResourceMonitorSubscriptionUrl" ? "normalizeResourceMonitorQuery"
    : name === "buildResourceProcessDetailsSubscriptionUrl" ? "normalizeResourceBreakdownQuery"
    : name === "buildMetricSnapshotSubscriptionUrl" ? "normalizeMetricSnapshotQuery"
    : name === "buildMetricFieldSubscriptionUrl" ? "normalizeMetricFieldQuery"
    : name === "buildGpuSpecializedTelemetrySubscriptionUrl" ? "normalizeGpuSpecializedTelemetryQuery"
    : null;
  const normalize = normalizerName ? module![normalizerName] : null;
  // The name and query are paired by getBuilderVariants; retain the original
  // dynamic invocation while asserting only that boundary's callable signature.
  return typeof normalize === "function" ? (normalize as (value: BuilderQuery) => BuilderQuery)(query) : query;
}

function uniqueStrings(values: readonly unknown[]): string[] {
  return [...new Set(values.filter((value): value is string => typeof value === "string" && value.length > 0))].sort();
}

async function deriveResourceQuerySelectors(catalogs: Catalogs): Promise<ResourceQuerySelectors> {
  const [dashboardPreferences, resourceTableSources, resourceBarSources, monitorConfig, fieldCards, cpuFields, deviceFields] = await (resourceSelectorSourcesPromise ??= Promise.all([
    import(pathToFileURL(dashboardPreferencesPath).href) as Promise<ResourceSelectorModules[0]>,
    import(pathToFileURL(resourceTableSourcesPath).href) as Promise<ResourceSelectorModules[1]>,
    import(pathToFileURL(resourceBarSourcesPath).href) as Promise<ResourceSelectorModules[2]>,
    import(pathToFileURL(monitorConfigPath).href) as Promise<ResourceSelectorModules[3]>,
    import(pathToFileURL(fieldCardsPath).href) as Promise<ResourceSelectorModules[4]>,
    import(pathToFileURL(cpuFieldsPath).href) as Promise<ResourceSelectorModules[5]>,
    import(pathToFileURL(deviceFieldsPath).href) as Promise<ResourceSelectorModules[6]>
  ]));
  // These production selector helpers only consume id, selectable, scopeKind and
  // scopeKey (the latter three are already optional on MetricDefinition). Their
  // wider declared catalog type also requires display metadata they never read.
  // Keep raw/id-only catalogs unchanged, without inventing that display metadata.
  const metrics = (Array.isArray(catalogs.metrics) ? catalogs.metrics : []) as MetricDefinition[];
  const settings = dashboardPreferences.dashboardPointValues(metrics);
  const available = (column: ResourceTableColumnSettings) => dashboardPreferences.dashboardMetricAvailable(column.id, column.binding, metrics);
  const bars = settings.resourceBars.filter(bar => dashboardPreferences.dashboardMetricAvailable(bar.metricId, bar.binding, metrics))
    .map(resourceBarSources.resourceBarQuery);
  const columns = monitorConfig.normalizeResourceTableColumns(settings.resourceTableColumns, metrics, "software");
  const metricIds = uniqueStrings(settings.cards.flatMap(card => card.slots.filter(slot =>
    !fieldCards.fieldCardSlot(slot.metricId, slot.binding)
      && dashboardPreferences.dashboardMetricAvailable(slot.metricId, slot.binding, metrics)).map(slot => slot.metricId)));
  const cardFields = uniqueStrings(fieldCards.fieldCardSlots(settings.cards).map(slot => slot.fieldName));
  // These are the actual field sets consumed by the CPU and device details pages,
  // not a concatenation of every device/field in the hardware catalog.
  const fieldQueries = [cardFields, cpuFields.numaFieldNames, [cpuFields.effectiveFrequencyFieldName], deviceFields.deviceMetricFieldNames]
    .filter(fields => fields.length > 0).map(fields => ({ fields }));
  return {
    metricIds,
    fieldQueries,
    processDetailLevel: columns.some(column => column.visible && ["user", "architecture"].includes(column.id)) ? "full" : "basic",
    tableItems: uniqueStrings([
      ...(columns.some(column => column.visible) ? ["process.inventory", "process.attribution"] : []),
      ...columns.filter(column => column.visible && available(column)).map(resourceTableSources.resourceTableMetricId).filter(id => id !== null)
    ]),
    bars,
    residualDetailMetricIds: resourceBarSources.resourceBarResidualDetailMetricIds(bars)
  };
}

async function readSubscriptionEndpoint(): Promise<string> {
  const text = await readFile(subscriptionChannelPath, "utf8");
  const source = ts.createSourceFile(subscriptionChannelPath, text, ts.ScriptTarget.Latest, true);
  for (const statement of source.statements) {
    if (!ts.isVariableStatement(statement)) continue;
    for (const declaration of statement.declarationList.declarations) {
      if (ts.isIdentifier(declaration.name)
        && declaration.name.text === "subscriptionEndpoint"
        && literalString(declaration.initializer) === "/api/subscriptions/stream") {
        return "/api/subscriptions/stream";
      }
    }
  }
  throw new Error("Production unified subscription endpoint changed; update the recorder allowlist deliberately");
}

function isSafeReadPath(path: unknown): path is string {
  if (typeof path !== "string" || !path.startsWith("/api/") || path.startsWith("//") || deniedPath.test(path)) return false;
  const url = new URL(path, "http://localhost");
  return url.origin === "http://localhost" && !url.username && !url.password && !url.hash;
}

function isSafeSubscriptionPath(path: unknown): path is string {
  if (!isSafeReadPath(path) || !new URL(path, "http://localhost").pathname.endsWith("/subscribe")) return false;
  return true;
}

function hasExportModifier(node: ts.FunctionDeclaration | ts.VariableStatement): boolean {
  return node.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword) === true;
}

function propertyNameOf(name: ts.PropertyName): string | null {
  if (ts.isIdentifier(name) || ts.isStringLiteral(name) || ts.isNumericLiteral(name)) return name.text;
  return null;
}

function parameterNameOf(parameter: ts.ParameterDeclaration): string {
  return ts.isIdentifier(parameter.name) ? parameter.name.text : parameter.name.getText();
}

function literalString(node: ts.Node | undefined): string | null {
  return node && (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) ? node.text : null;
}
