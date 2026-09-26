export default async function usabilityProbe(page) {
  const result = await page.evaluate(() => {
    const pageId = document.body.dataset.page ?? "unknown";
    const shell = document.querySelector(".app-shell");
    const activePage = document.querySelector(".active-page");
    const table = document.querySelector(".resource-table-frame[role='table']");
    const resourcePerformancePanel = document.querySelector(".resource-performance-panel");
    const resourceListRegion = document.querySelector(
      '.monitor-work-region[aria-label="资源列表"]');
    const resourceListObservation = resourceListRegion?.querySelector(".observation-state");
    const resourceListContentState = resourceListRegion?.querySelector(
      ".content-state.empty, .content-state.waiting, .content-state.unavailable");
    const visibleDialog = Array.from(document.querySelectorAll("[role='dialog'][aria-modal='true']"))
      .some(element => {
        const rect = element.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
      });
    const activeElement = document.activeElement;
    const visibleUnnamedInputs = Array.from(document.querySelectorAll("input, select, textarea"))
      .filter(element => {
        const rect = element.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return false;
        if (element.getAttribute("aria-label") || element.getAttribute("aria-labelledby")) return false;
        const id = element.getAttribute("id");
        if (id && document.querySelector(`label[for='${CSS.escape(id)}']`)) return false;
        const implicitLabel = element.closest("label");
        return !implicitLabel || !implicitLabel.textContent?.trim();
      })
      .map(element => element.outerHTML.slice(0, 240));
    const cards = Array.from(document.querySelectorAll(".metric-card"))
      .map(element => {
        const rect = element.getBoundingClientRect();
        return { left: rect.left, right: rect.right, width: rect.width };
      });
    const shellRect = shell?.getBoundingClientRect() ?? null;
    const shellOverflowCandidates = shellRect
      ? Array.from(shell.querySelectorAll("*"))
        .filter(element => {
          const rect = element.getBoundingClientRect();
          return rect.width > 0
            && rect.height > 0
            && (rect.right > shellRect.right + 1 || rect.left < shellRect.left - 1);
        })
        .slice(0, 20)
        .map(element => {
          const rect = element.getBoundingClientRect();
          return {
            tag: element.tagName,
            className: element.className,
            text: element.textContent?.trim().slice(0, 120) ?? "",
            left: rect.left,
            right: rect.right,
            width: rect.width,
            clientWidth: element.clientWidth,
            scrollWidth: element.scrollWidth,
            ancestors: Array.from({ length: 5 }, (_, index) => index)
              .reduce((items, _, index) => {
                const ancestor = index === 0
                  ? element.parentElement
                  : items[index - 1]?.element?.parentElement ?? null;
                if (!ancestor) {
                  return items;
                }
                const ancestorRect = ancestor.getBoundingClientRect();
                items.push({
                  element: ancestor,
                  tag: ancestor.tagName,
                  className: ancestor.className,
                  left: ancestorRect.left,
                  right: ancestorRect.right,
                  width: ancestorRect.width,
                  clientWidth: ancestor.clientWidth,
                  scrollWidth: ancestor.scrollWidth
                });
                return items;
              }, [])
              .map(({ element: _element, ...ancestor }) => ancestor)
          };
        })
      : [];

    return {
      pageId,
      viewport: { width: innerWidth, height: innerHeight },
      document: {
        clientWidth: document.documentElement.clientWidth,
        scrollWidth: document.documentElement.scrollWidth
      },
      shell: shell && {
        clientWidth: shell.clientWidth,
        scrollWidth: shell.scrollWidth,
        overflowX: getComputedStyle(shell).overflowX
      },
      activePage: activePage && {
        left: activePage.getBoundingClientRect().left,
        right: activePage.getBoundingClientRect().right,
        clientWidth: activePage.clientWidth,
        scrollWidth: activePage.scrollWidth
      },
      cards,
      shellOverflowCandidates,
      table: table && {
        rowCount: table.getAttribute("aria-rowcount"),
        columnCount: table.getAttribute("aria-colcount"),
        rowGroups: table.querySelectorAll("[role='rowgroup']").length,
        rows: table.querySelectorAll("[role='row']:not([aria-hidden='true'])").length,
        columnHeaders: table.querySelectorAll("[role='columnheader']").length,
        cells: table.querySelectorAll(
          "[role='row']:not([aria-hidden='true']) [role='cell']").length
      },
      resourceListContentState: resourceListRegion && {
        hasTable: Boolean(table),
        hasPerformancePanel: Boolean(resourcePerformancePanel),
        contentStatus: resourceListContentState
          ? Array.from(resourceListContentState.classList)
            .find(value => ["empty", "waiting", "unavailable"].includes(value)) ?? null
          : null,
        observationStatus: resourceListObservation
          ? Array.from(resourceListObservation.classList)
            .find(value => ["loading", "stale", "error", "profile-disabled"].includes(value)) ?? null
          : null
      },
      visibleUnnamedInputs,
      visibleDialog,
      focus: activeElement && {
        tag: activeElement.tagName,
        name: activeElement.getAttribute("aria-label") || activeElement.textContent?.trim() || "",
        outlineStyle: getComputedStyle(activeElement).outlineStyle
      }
    };
  });

  if (result.document.scrollWidth > result.document.clientWidth) {
    throw new Error(`Document has horizontal overflow: ${JSON.stringify(result.document)}`);
  }
  if (result.shell && result.shell.scrollWidth > result.shell.clientWidth) {
    throw new Error(`Application shell has horizontal overflow: ${JSON.stringify({
      ...result.shell,
      candidates: result.shellOverflowCandidates
    })}`);
  }
  if (result.activePage && result.activePage.right > result.viewport.width + 1) {
    throw new Error(`Active page extends outside the viewport: ${JSON.stringify(result.activePage)}`);
  }
  if (result.cards.some(card => card.left < -1 || card.right > result.viewport.width + 1)) {
    throw new Error(`Dashboard card extends outside the viewport: ${JSON.stringify(result.cards)}`);
  }
  if (result.pageId === "monitor" && result.table &&
      (result.table.rowGroups !== 2 || result.table.columnHeaders === 0 ||
        !Number.isInteger(Number(result.table.rowCount)) ||
        Number(result.table.rowCount) < result.table.rows ||
        Number(result.table.columnCount) !== result.table.columnHeaders)) {
    throw new Error(`Resource table semantics are incomplete: ${JSON.stringify(result.table)}`);
  }
  if (result.pageId === "monitor" && !result.visibleDialog &&
      (!result.resourceListContentState ||
        (!result.resourceListContentState.hasTable &&
          !result.resourceListContentState.hasPerformancePanel &&
          !result.resourceListContentState.contentStatus &&
          !result.resourceListContentState.observationStatus))) {
    throw new Error(
      `Resource table has no explicit content state: ${JSON.stringify(result.resourceListContentState)}`);
  }
  if (result.visibleUnnamedInputs.length > 0) {
    throw new Error(`Visible form controls lack names: ${JSON.stringify(result.visibleUnnamedInputs)}`);
  }

  return result;
}
