// typescript-api is the 6.x compiler under an alias: TypeScript 7.x ships no JavaScript compiler API (its package exports
// only the version). Remove the alias when 7.x ships a stable compiler API: port this file to it and drop typescript-api.
import ts from "typescript-api";
import { readFile, readdir } from "node:fs/promises";
import { dirname, extname, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

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
const resourceBreakdownQuery = {
  bars: null,
  processDetailSoftwareIds: [],
  residualDetailMetricIds: []
};

let sourceGraphPromise;
let resourceSelectorSourcesPromise;

/**
 * Return only production read descriptors and subscription URLs reachable from
 * FrontendSources. Catalog-backed selectors are expanded by the same production
 * builders once the recorder has fetched those catalogs.
 */
export async function deriveProductionReadPlan(intervalMs = 1000, catalogs = null) {
  if (!Number.isFinite(intervalMs) || intervalMs < 1) {
    throw new Error("Subscription interval must be a positive finite number");
  }
  const sources = await (sourceGraphPromise ??= discoverProductionSources());
  const reads = new Set();
  const subscriptions = new Set();
  const catalogReads = {};
  const observationReads = new Set(), observationSubscriptions = new Set();
  const resourceSelectors = catalogs ? await deriveResourceQuerySelectors(catalogs) : null;
  let discoveryPath = null;

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
        const pathValue = module[builder.name](...args);
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
      const pending = [statement];
      while (pending.length) {
        const node = pending.pop();
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
function isObservationSource(path, key = "") {
  const module = relative(sourceRoot, path).split(sep).join("/");
  return /^(data\/(monitor|metricFields|resourceMonitor|cpu|gpu|localSystem)\/|features\/deviceTopology\/)/.test(module)
    && !/override|score|binding|settings|control/i.test(key ?? "");
}

export function assertAllowedRequest(plan, method, path, body) {
  if (method === "GET" && plan.reads.includes(path) && body === undefined) return;
  const expected = {
    version: 1,
    subscriptions: plan.subscriptions.map((subscriptionPath, index) => ({ id: `slice:${index}`, path: subscriptionPath }))
  };
  if (method === "POST" && path === plan.streamPath && JSON.stringify(body) === JSON.stringify(expected)) return;
  throw new Error(`Recorder refused ${method} ${path}`);
}

async function discoverProductionSources() {
  const queue = [ownerPath];
  const visited = new Set();
  const sources = [];
  while (queue.length) {
    if (visited.size >= 1000) throw new Error("Production API module limit exceeded");
    const path = queue.pop();
    if (visited.has(path)) continue;
    visited.add(path);
    const text = await readFile(path, "utf8");
    const source = ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true);
    const isOwner = path === ownerPath;
    if (isOwner || isRelevantDataModule(path)) {
      const module = isOwner ? null : await import(pathToFileURL(path).href);
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
  const readSources = [];
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

async function findTypeScriptFiles(root) {
  const pending = [root];
  const files = [];
  let visitedDirectories = 0;
  while (pending.length) {
    if (++visitedDirectories > 1000 || files.length > 5000) throw new Error("Frontend source file limit exceeded");
    const directory = pending.pop();
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) pending.push(path);
      else if (entry.isFile() && (entry.name.endsWith(".ts") || entry.name.endsWith(".tsx"))) files.push(path);
    }
  }
  return files.sort();
}

function resolveImportedTypeScript(directory, specifier) {
  const target = resolve(directory, specifier);
  if (extname(target)) return target.endsWith(".ts") ? target : null;
  return `${target}.ts`;
}

function isRelevantDataModule(path) {
  const relativePath = relative(sourceRoot, path);
  if (!relativePath || relativePath === ".." || relativePath.startsWith(`..${sep}`)) return false;
  const firstSegment = relativePath.split(sep, 1)[0];
  return (firstSegment === "data" || firstSegment === "features") && path.endsWith(".ts");
}

function findGetDescriptors(source) {
  const result = [];
  const pending = [...source.statements];
  let visitedNodes = 0;
  while (pending.length) {
    if (++visitedNodes > 200_000) throw new Error("Production API syntax tree limit exceeded");
    const node = pending.pop();
    if (ts.isObjectLiteralExpression(node)) {
      const properties = new Map();
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

function isRequestClientDescriptor(node) {
  const call = node.parent;
  return ts.isCallExpression(call) && call.arguments[0] === node
    && ts.isPropertyAccessExpression(call.expression) && call.expression.name.text === "request";
}

function isDefaultOrExplicitGet(request) {
  if (!request) return true;
  if (!ts.isObjectLiteralExpression(request) || request.properties.some(property => ts.isSpreadAssignment(property)
    || (property.name && ts.isComputedPropertyName(property.name)))) return false;
  const method = request.properties.find(property => property.name && propertyNameOf(property.name) === "method");
  if (method) return ts.isPropertyAssignment(method) && literalString(method.initializer) === "GET";
  return !request.properties.some(property => property.name && propertyNameOf(property.name) === "body");
}

function literalUrlAlternatives(node) {
  if (!node) return [];
  const valuesByNode = new Map();
  const pending = [{ node, visited: false }];
  let visitedNodes = 0;
  while (pending.length) {
    if (++visitedNodes > 100_000) throw new Error("Production URL expression limit exceeded");
    const current = pending.pop();
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

function uniqueUrlValues(values) {
  return [...new Set(values)];
}

function callNameOf(expression) {
  if (ts.isIdentifier(expression)) return expression.text;
  return ts.isPropertyAccessExpression(expression) ? expression.name.text : "";
}

function isExplicitOrDefaultGet(options) {
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

function findExportedSubscriptionBuilders(source) {
  const builders = [];
  for (const statement of source.statements) {
    if (ts.isFunctionDeclaration(statement)
      && hasExportModifier(statement)
      && /^build.*SubscriptionUrl$/.test(statement.name?.text ?? "")) {
      builders.push({ name: statement.name.text, parameters: statement.parameters });
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

function getBuilderVariants(name, catalogs, module, resourceSelectors) {
  if (name === "buildResourceMonitorSubscriptionUrl") {
    if (!resourceSelectors) return [];
    return [
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
    ].filter(query => query.scope === "table" ? resourceSelectors.tableItems.length > 0 : resourceSelectors.bars.length > 0);
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
    const counterIds = typeof module.buildGpuSpecializedCounterIds === "function"
      ? uniqueStrings(module.buildGpuSpecializedCounterIds(indexes))
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

function normalizeBuilderQuery(name, query, module) {
  const normalizerName = name === "buildResourceMonitorSubscriptionUrl" ? "normalizeResourceMonitorQuery"
    : name === "buildResourceProcessDetailsSubscriptionUrl" ? "normalizeResourceBreakdownQuery"
    : name === "buildMetricSnapshotSubscriptionUrl" ? "normalizeMetricSnapshotQuery"
    : name === "buildMetricFieldSubscriptionUrl" ? "normalizeMetricFieldQuery"
    : name === "buildGpuSpecializedTelemetrySubscriptionUrl" ? "normalizeGpuSpecializedTelemetryQuery"
    : null;
  const normalize = normalizerName ? module[normalizerName] : null;
  return typeof normalize === "function" ? normalize(query) : query;
}

function uniqueStrings(values) {
  return [...new Set(values.filter(value => typeof value === "string" && value.length > 0))].sort();
}

async function deriveResourceQuerySelectors(catalogs) {
  const [dashboardPreferences, resourceTableSources, resourceBarSources, monitorConfig, fieldCards, cpuFields, deviceFields] = await (resourceSelectorSourcesPromise ??= Promise.all([
    import(pathToFileURL(dashboardPreferencesPath).href),
    import(pathToFileURL(resourceTableSourcesPath).href),
    import(pathToFileURL(resourceBarSourcesPath).href),
    import(pathToFileURL(monitorConfigPath).href),
    import(pathToFileURL(fieldCardsPath).href),
    import(pathToFileURL(cpuFieldsPath).href),
    import(pathToFileURL(deviceFieldsPath).href)
  ]));
  const metrics = Array.isArray(catalogs.metrics) ? catalogs.metrics : [];
  const settings = dashboardPreferences.dashboardPointValues(metrics);
  const available = column => dashboardPreferences.dashboardMetricAvailable(column.id, column.binding, metrics);
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

async function readSubscriptionEndpoint() {
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

function isSafeReadPath(path) {
  if (typeof path !== "string" || !path.startsWith("/api/") || path.startsWith("//") || deniedPath.test(path)) return false;
  const url = new URL(path, "http://localhost");
  return url.origin === "http://localhost" && !url.username && !url.password && !url.hash;
}

function isSafeSubscriptionPath(path) {
  if (!isSafeReadPath(path) || !new URL(path, "http://localhost").pathname.endsWith("/subscribe")) return false;
  return true;
}

function hasExportModifier(node) {
  return node.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword) === true;
}

function propertyNameOf(name) {
  if (ts.isIdentifier(name) || ts.isStringLiteral(name) || ts.isNumericLiteral(name)) return name.text;
  return null;
}

function parameterNameOf(parameter) {
  return ts.isIdentifier(parameter.name) ? parameter.name.text : parameter.name.getText();
}

function literalString(node) {
  return node && (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) ? node.text : null;
}
