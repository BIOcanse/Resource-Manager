import type { AppCopy } from "../zh/index.ts";

export const enSoftwareCopy: Pick<
  AppCopy,
  "softwareKind" | "managementRole" | "management" | "managementPage" | "manualSoftware"
> = {
  managementPage: {
    browserRuntimeTab: "Runtime management",
    migrationTab: "Migration workbench",
    pageLabel: (kind: string) => `${kind} management`,
    searchLabel: (kind: string) => `Search ${kind}`,
    search: "Search",
    itemCount: (count: number) => `${count} items`,
    countUnavailable: "--",
    softwareRegistrySupplement: "Software registry additions",
    softwareRegistrySupplementDisabled: "This startup profile only shows the component catalog and does not load software registry additions.",
    operationState: "Operation state",
    componentCatalog: "Component catalog",
    softwareRegistry: "Software registry",
    viewOnly: "This startup profile is view-only.",
    detailsOf: (name: string) => `Details: ${name}`,
    moreActionsOf: (name: string) => `More actions: ${name}`,
    currentSoftware: "this software",
    rootNeedsCheck: "Root directory needs a check",
    details: "Details",
    moreActions: "More actions"
  },
  softwareKind: {
    Adapted: "Adapted software",
    Controlled: "Controlled software",
    Unconfirmed: "Unconfirmed",
    Managed: "Managed",
    Game: "Games",
    HighPerformance: "High-performance software",
    Other: "General applications",
    WindowsSystem: "Windows system",
    WindowsComponent: "Windows app/component",
    WindowsService: "Windows service",
    RuntimePackage: "Windows app",
    RuntimeProduct: "General applications",
    RuntimeRoot: "General applications",
    Unattributed: "Unattributed processes",
    Empty: "Free"
  },
  managementRole: {
    Dependency: "Dependencies",
    Support: "Support"
  },
  management: {
    categoryNav: "Component and software categories",
    add: "Add",
    refresh: "Refresh",
    refreshing: "Refreshing",
    emptyCategory: "This category has no records.",
    noSearchResults: (query: string) => `No records match “${query}”.`,
    clearSearch: "Clear search",
    install: "Install",
    uninstall: "Uninstall",
    downloadAndInstall: "Download and install",
    obtainInstaller: "Get the installer",
    installed: "Installed",
    settingsAndMigration: "Settings and migration",
    issueStrip: "Software issues",
    issueHeading: "Issue notice",
    issueCurrentReport: "Current report",
    issueKnownCatalog: "Known issue catalog",
    issueReferences: (label: string) => `${label} references`
  },
  manualSoftware: {
    addAdapted: "Add adapted software",
    addGame: "Add game",
    addHighPerformance: "Add high-performance software",
    addGeneral: "Add general application"
  }
};
