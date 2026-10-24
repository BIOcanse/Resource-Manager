import type { AppCopy } from "../zh/index.ts";

export const enSoftwareCopy: Pick<
  AppCopy,
  "softwareKind" | "managementRole" | "management" | "managementPage" | "manualSoftware"
> = {
  managementPage: {
    browserRuntimeTab: "Runtimes",
    migrationTab: "Migration workbench",
    pageLabel: (kind: string) => `${kind} management`,
    searchLabel: (kind: string) => `Search ${kind}`,
    search: "Search",
    itemCount: (count: number) => `${count} items`,
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
    Unclassified: "Unclassified",
    Adapted: "Adapted",
    Controlled: "Controlled",
    Unconfirmed: "Unconfirmed",
    Managed: "Managed",
    Game: "Games",
    HighPerformance: "High-performance",
    Other: "Apps",
    WindowsSystem: "Windows system",
    WindowsComponent: "Windows app/component",
    WindowsService: "Windows service",
    RuntimePackage: "Windows app",
    RuntimeProduct: "Apps",
    RuntimeRoot: "Apps",
    Unattributed: "Unattributed processes",
    SystemResidual: "System/driver reserved",
    Empty: "Free"
  },
  managementRole: {
    Dependency: "Dependencies",
    Support: "Support"
  },
  management: {
    classifySoftware: "Choose category",
    unclassifiedReminder: "Identified at runtime without a known category. Choose a category or leave it for later.",
    discoveredPackage: "Identified by a running app package",
    discoveredProduct: "Identified by the program product name",
    discoveredRoot: "Identified by the running installation root",
    runtimeDiscovery: "Runtime discovery",

    categoryNav: "Component and software categories",
    add: "Add",
    refresh: "Refresh",
    refreshing: "Refreshing",
    emptyCategory: "This category has no records.",
    noSearchResults: (query: string) => `No records match “${query}”.`,
    clearSearch: "Clear search",
    install: "Install",
    uninstall: "Uninstall",
    verify: "Verify and fetch required files",
    downloadAndInstall: "Download and install",
    obtainInstaller: "Get the installer",
    installed: "Installed",
    softwareSettings: "Software settings",
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
