import type { Journey, JourneyEntry } from "./journeyTypes.ts";
import managementSourceStatesJourney from "./managementSourceStatesJourney.ts";
import fanManagementModesJourney from "./fanManagementModesJourney.ts";
import frontendRefreshIntervalsJourney from "./frontendRefreshIntervalsJourney.ts";
import productUpdatesJourney from "./productUpdatesJourney.ts";
import updateNoticeJourney from "./updateNoticeJourney.ts";
import managementInventoryRevisionJourney from "./managementInventoryRevisionJourney.ts";
import diskUsageReadRecoveryJourney from "./diskUsageReadRecoveryJourney.ts";
import diskUsageLocalTreemapJourney from "./diskUsageLocalTreemapJourney.ts";
import diskUsagePagedTreeJourney from "./diskUsagePagedTreeJourney.ts";
import diskUsageActionErrorsJourney from "./diskUsageActionErrorsJourney.ts";
import diskUsageSetupOrderJourney from "./diskUsageSetupOrderJourney.ts";
import overlaySettingsJourney from "./overlaySettingsJourney.ts";
import performanceOverlaySettingsJourney from "./performanceOverlaySettingsJourney.ts";
import loginStartupJourney from "./loginStartupJourney.ts";
import managementFeedbackJourney from "./managementFeedbackJourney.ts";
import reportRuleSettingsJourney from "./reportRuleSettingsJourney.ts";
import trustedAccessJourney from "./trustedAccessJourney.ts";
import trustedAccessRecoveryJourney from "./trustedAccessRecoveryJourney.ts";
import softwareUnclassifiedJourney from "./softwareUnclassifiedJourney.ts";
import frontendAuditJourney from "./frontendAuditJourney.ts";
import stableButtonLabelsJourney from "./stableButtonLabelsJourney.ts";
import { runBrowserGate } from "./browserGateRunner.ts";
import cpuBaselineRatioJourney from "./cpuBaselineRatioJourney.ts";
import cpuCurrentValueSubscriptionJourney from "./cpuCurrentValueSubscriptionJourney.ts";
import cpuManualPlacementSubscriptionJourney from "./cpuManualPlacementSubscriptionJourney.ts";
import creditsJourney from "./creditsJourney.ts";
import currentValueSubscriptionJourney from "./currentValueSubscriptionJourney.ts";
import monitorCurrentValueRetentionJourney from "./monitorCurrentValueRetentionJourney.ts";
import deviceTopologySourceJourney from "./deviceTopologySourceJourney.ts";
import diskUsageFeedbackRefreshJourney from "./diskUsageFeedbackRefreshJourney.ts";
import diskUsageOperationInvalidationJourney from "./diskUsageOperationInvalidationJourney.ts";
import frontendUsabilityJourney from "./frontendUsabilityJourney.ts";
import frontendVisibilityDemandJourney from "./frontendVisibilityDemandJourney.ts";
import optimizationReportsJourney from "./optimizationReportsJourney.ts";
import r2RemediationJourney from "./r2RemediationJourney.ts";
import resourceBarColorModesJourney from "./resourceBarColorModesJourney.ts";
import resourceBarProportionJourney from "./resourceBarProportionJourney.ts";
import controlReadingsJourney from "./controlReadingsJourney.ts";
import controlCardJourney from "./controlCardJourney.ts";
import controlAuditJourney from "./controlAuditJourney.ts";
import controlNumberInitialJourney from "./controlNumberInitialJourney.ts";
import controlSlowRequestJourney from "./controlSlowRequestJourney.ts";
import controlToggleChoiceJourney from "./controlToggleChoiceJourney.ts";
import controlSliderInteractionJourney from "./controlSliderInteractionJourney.ts";
import metricFieldsJourney from "./metricFieldsJourney.ts";
import metricFieldCardsJourney from "./metricFieldCardsJourney.ts";
import metricFieldFailureJourney from "./metricFieldFailureJourney.ts";
import metricPickerGpuNamesJourney from "./metricPickerGpuNamesJourney.ts";
import gpuUnknownRuntimeJourney from "./gpuUnknownRuntimeJourney.ts";
import gpuCommittedThresholdsJourney from "./gpuCommittedThresholdsJourney.ts";
import smartDetailsPlacementFactsJourney from "./smartDetailsPlacementFactsJourney.ts";
import smartDetailsAllRecordsJourney from "./smartDetailsAllRecordsJourney.ts";
import performanceHistoryJourney from "./performanceHistoryJourney.ts";
import resourceLayoutTaskJourney from "./resourceLayoutTaskJourney.ts";
import resourceTableContentStateJourney from "./resourceTableContentStateJourney.ts";
import resourceTableScrollStabilityJourney from "./resourceTableScrollStabilityJourney.ts";
import resourceTableRowsJourney from "./resourceTableRowsJourney.ts";
import columnResizeInteractionJourney from "./columnResizeInteractionJourney.ts";
import fanCurveInteractionJourney from "./fanCurveInteractionJourney.ts";
import switchInteractionJourney from "./switchInteractionJourney.ts";
import reorderInteractionJourney from "./reorderInteractionJourney.ts";
import animationStyleJourney from "./animationStyleJourney.ts";
import pageMotionJourney, { pageMotionJourneyIn } from "./pageMotionJourney.ts";
import settingsSourceJourney from "./settingsSourceJourney.ts";
import taskManagerCopyJourney from "./taskManagerCopyJourney.ts";
import settingsSectionsLayoutJourney from "./settingsSectionsLayoutJourney.ts";
import aiGatewayKeysJourney from "./aiGatewayKeysJourney.ts";
import gpuOverflowSettingsJourney from "./gpuOverflowSettingsJourney.ts";
import realtimeActionBudgetSettingsJourney from "./realtimeActionBudgetSettingsJourney.ts";
import serviceRecoverySettingsJourney from "./serviceRecoverySettingsJourney.ts";
import softwareIssueJourney from "./softwareIssueJourney.ts";
import softwareParticipationJourney from "./softwareParticipationJourney.ts";
import taskCenterJourney from "./taskCenterJourney.ts";
import taskCenterStatusIconJourney from "./taskCenterStatusIconJourney.ts";
import managementSharedRefreshJourney from "./managementSharedRefreshJourney.ts";
import targetedRecordingSourceJourney from "./targetedRecordingSourceJourney.ts";
import themeConsistencyJourney from "./themeConsistencyJourney.ts";
import pageTabsConsistencyJourney from "./pageTabsConsistencyJourney.ts";
import uiPrimitivesJourney from "./uiPrimitivesJourney.ts";
import localizationJourney from "./localizationJourney.ts";
import accessibilitySemanticsJourney from "./accessibilitySemanticsJourney.ts";

// 旅程登记表：[名称, (page, baseUrl, options) => 结果]。运行机制（工作进程、按语言拆分、输出和计时）在
// browserGateRunner.ts，用法见那里的说明。
const lazyJourney = (load: () => Promise<{ default: Journey }>): Journey =>
  async (...args) => (await load()).default(...args);

const allJourneys: JourneyEntry[] = [
  ["core-lease-contract", lazyJourney(() => import("./coreLeaseContractJourney.ts"))],
  ["page-value-retention", lazyJourney(() => import("./pageValueRetentionJourney.ts"))],
  ["kept-page", lazyJourney(() => import("./keptPageJourney.ts"))],
  ["realistic-data-sweep", lazyJourney(() => import("./realisticDataSweepJourney.ts"))],
  ["management-source-states", managementSourceStatesJourney],
  ["monitor-page-task", lazyJourney(() => import("./monitorPageTaskJourney.ts"))],
  ["live-keyboard-focus", lazyJourney(() => import("./liveKeyboardFocusJourney.ts"))],
  ["resource-table-totals-locale", lazyJourney(() => import("./resourceTableTotalsLocaleJourney.ts"))],
  ["resource-table-header-fit", lazyJourney(() => import("./resourceTableHeaderFitJourney.ts"))],
  ["help-hint", lazyJourney(() => import("./helpHintJourney.ts"))],
  ["resource-bar-placeholder", lazyJourney(() => import("./resourceBarPlaceholderJourney.ts"))],
  ["placeholder-sizes", lazyJourney(() => import("./placeholderSizesJourney.ts"))],
  ["own-tooltips", lazyJourney(() => import("./ownTooltipsJourney.ts"))],
  ["interface-number-locale", lazyJourney(() => import("./interfaceNumberLocaleJourney.ts"))],
  ["shell-title-selection", lazyJourney(() => import("./shellTitleSelectionJourney.ts"))],
  ["dashboard-preferences", lazyJourney(() => import("./dashboardPreferencesJourney.ts"))],
  ["service-recovery-settings", serviceRecoverySettingsJourney],
  ["product-updates", productUpdatesJourney],
  ["update-notice", updateNoticeJourney],
  ["language-load-failure", lazyJourney(() => import("./languageLoadFailureJourney.ts"))],
  ["frontend-refresh-intervals", frontendRefreshIntervalsJourney],
  ["management-inventory-revisions", managementInventoryRevisionJourney],
  ["disk-usage-read-recovery", diskUsageReadRecoveryJourney],
  ["disk-usage-local-treemap", diskUsageLocalTreemapJourney],
  ["disk-usage-paged-tree", diskUsagePagedTreeJourney],
  ["disk-usage-action-errors", diskUsageActionErrorsJourney],
  ["disk-usage-setup-order", diskUsageSetupOrderJourney],
  ["overlay-settings", overlaySettingsJourney],
  ["performance-overlay-settings", performanceOverlaySettingsJourney],
  ["login-startup", loginStartupJourney],
  ["management-feedback", managementFeedbackJourney],
  ["management-shared-refresh", managementSharedRefreshJourney],
  ["frontend-audit", frontendAuditJourney],
  ["report-rule-settings", reportRuleSettingsJourney],
  ["trusted-access", trustedAccessJourney],
  ["trusted-access-recovery", trustedAccessRecoveryJourney],
  ["localization", localizationJourney],
  ["credits", creditsJourney],
  ["frontend-usability", frontendUsabilityJourney],
  ["ui-primitives", uiPrimitivesJourney],
  ["stable-button-labels", stableButtonLabelsJourney],
  ["settings-source", settingsSourceJourney],
  ["task-manager-copy", taskManagerCopyJourney],
  ["settings-sections-layout", settingsSectionsLayoutJourney],
  ["ai-gateway-keys", aiGatewayKeysJourney],
  ["gpu-overflow-settings", gpuOverflowSettingsJourney],
  ["realtime-action-budget-settings", realtimeActionBudgetSettingsJourney],
  ["resource-bar-color-modes", resourceBarColorModesJourney],
  ["resource-bar-proportion", resourceBarProportionJourney],
  ["resource-bar-interaction", lazyJourney(() => import("./resourceBarInteractionJourney.ts"))],
  ["resource-bar-real-cadence", lazyJourney(() => import("./resourceBarRealCadenceJourney.ts"))],
  ["control-readings", controlReadingsJourney],
  ["fan-management-modes", fanManagementModesJourney],
  ["control-card", controlCardJourney],
  ["control-audit", controlAuditJourney],
  ["control-number-initial", controlNumberInitialJourney],
  ["control-slow-request", controlSlowRequestJourney],
  ["control-toggle-choice", controlToggleChoiceJourney],
  ["control-slider-interaction", controlSliderInteractionJourney],
  ["metric-fields", metricFieldsJourney],
  ["metric-field-cards", metricFieldCardsJourney],
  ["metric-field-failure", metricFieldFailureJourney],
  ["metric-catalog-push", lazyJourney(() => import("./metricCatalogPushJourney.ts"))],
  ["metric-card-states", lazyJourney(() => import("./metricCardStatesJourney.ts"))],
  ["metric-card-slots", lazyJourney(() => import("./metricCardSlotsJourney.ts"))],
  ["metric-source-choice", lazyJourney(() => import("./metricSourceChoiceJourney.ts"))],
  ["resource-table-source-choice", lazyJourney(() => import("./resourceTableSourceChoiceJourney.ts"))],
  ["resource-table-column-editor", lazyJourney(() => import("./resourceTableColumnEditorJourney.ts"))],
  ["resource-table-columns", lazyJourney(() => import("./resourceTableColumnsJourney.ts"))],
  ["physical-memory-residual", lazyJourney(() => import("./physicalMemoryResidualJourney.ts"))],
  ["resource-bar-reserved-selection", lazyJourney(() => import("./resourceBarReservedSelectionJourney.ts"))],
  ["resource-bar-pointer-geometry", lazyJourney(() => import("./resourceBarPointerGeometryJourney.ts"))],
  ["resource-bar-selection-toggle", lazyJourney(() => import("./resourceBarSelectionToggleJourney.ts"))],
  ["resource-table-totals", lazyJourney(() => import("./resourceTableTotalsJourney.ts"))],
  ["resource-bar-source-choice", lazyJourney(() => import("./resourceBarSourceChoiceJourney.ts"))],
  ["resource-bar-editor", lazyJourney(() => import("./resourceBarEditorJourney.ts"))],
  ["overlay-source-choice", lazyJourney(() => import("./overlaySourceChoiceJourney.ts"))],
  ["metric-picker-gpu-names", metricPickerGpuNamesJourney],
  ["gpu-names", lazyJourney(() => import("./gpuNamesJourney.ts"))],
  ["gpu-unknown-runtime", gpuUnknownRuntimeJourney],
  ["gpu-committed-thresholds", gpuCommittedThresholdsJourney],
  ["gpu-dedicated-memory-states", lazyJourney(() => import("./gpuDedicatedMemoryStatesJourney.ts"))],
  ["smart-details-placement-facts", smartDetailsPlacementFactsJourney],
  ["smart-details-all-records", smartDetailsAllRecordsJourney],
  ["smart-details-history", lazyJourney(() => import("./smartDetailsHistoryJourney.ts"))],
  ["software-participation", softwareParticipationJourney],
  ["software-protection-source", lazyJourney(() => import("./softwareProtectionSourceJourney.ts"))],
  ["software-base-score-source", lazyJourney(() => import("./softwareBaseScoreSourceJourney.ts"))],
  ["memory-protection-settings", lazyJourney(() => import("./memoryProtectionSettingsJourney.ts"))],
  ["memory-cpu-tier", lazyJourney(() => import("./memoryCpuTierJourney.ts"))],
  ["memory-protection-no-dump", lazyJourney(() => import("./memoryProtectionNoDumpJourney.ts"))],
  ["memory-protection-layout", lazyJourney(() => import("./memoryProtectionLayoutJourney.ts"))],
  ["memory-protection-events", lazyJourney(() => import("./memoryProtectionEventsJourney.ts"))],
  ["memory-protection-notifications", lazyJourney(() => import("./memoryProtectionNotificationsJourney.ts"))],
  ["memory-protection-points", lazyJourney(() => import("./memoryProtectionPointsJourney.ts"))],
  ["performance-history", performanceHistoryJourney],
  ["theme-consistency", themeConsistencyJourney],
  ["page-tabs-consistency", pageTabsConsistencyJourney],
  ["software-issues", softwareIssueJourney],
  ["software-unclassified", softwareUnclassifiedJourney],
  ["device-topology-source", deviceTopologySourceJourney],
  ["disk-usage-feedback-refresh", diskUsageFeedbackRefreshJourney],
  ["disk-usage-operation-invalidation", diskUsageOperationInvalidationJourney],
  ["targeted-recording-source", targetedRecordingSourceJourney],
  ["targeted-recordings", lazyJourney(() => import("./targetedRecordingsJourney.ts"))],
  ["kernel-leak-report", lazyJourney(() => import("./kernelLeakReportJourney.ts"))],
  ["cpu-baseline-ratio", cpuBaselineRatioJourney],
  ["cpu-residency-reasons", lazyJourney(() => import("./cpuResidencyReasonsJourney.ts"))],
  ["cpu-current-value-subscription", cpuCurrentValueSubscriptionJourney],
  ["cpu-manual-placement-subscription", cpuManualPlacementSubscriptionJourney],
  ["current-value-subscription", currentValueSubscriptionJourney],
  ["monitor-current-value-retention", monitorCurrentValueRetentionJourney],
  ["frontend-visibility-demand", frontendVisibilityDemandJourney],
  ["optimization-reports", optimizationReportsJourney],
  ["smart-failure", lazyJourney(() => import("./smartFailureJourney.ts"))],
  ["task-center", taskCenterJourney],
  ["task-center-status-icon", taskCenterStatusIconJourney],
  ["resource-layout-task", resourceLayoutTaskJourney],
  ["resource-table-content-state", resourceTableContentStateJourney],
  ["resource-table-scroll-stability", resourceTableScrollStabilityJourney],
  ["resource-table-rows", resourceTableRowsJourney],
  ["column-resize-interaction", columnResizeInteractionJourney],
  ["fan-curve-interaction", fanCurveInteractionJourney],
  ["gpu-vf-curve", lazyJourney(() => import("./gpuVfCurveJourney.ts"))],
  ["switch-interaction", switchInteractionJourney],
  ["wheel-accumulation", lazyJourney(() => import("./wheelAccumulationJourney.ts"))],
  ["reorder-interaction", reorderInteractionJourney],
  ["animation-style", animationStyleJourney],
  ["interaction-parity", lazyJourney(() => import("./interactionParityJourney.ts"))],
  ["page-motion", pageMotionJourney],
  ["page-motion-desktop", pageMotionJourneyIn("desktop")],
  ["page-motion-terminal", pageMotionJourneyIn("terminal")],
  ["page-motion-timing", lazyJourney(() => import("./pageMotionTimingJourney.ts"))],
  ["r2-remediation", r2RemediationJourney],
  ["accessibility-semantics", accessibilitySemanticsJourney]
];
await runBrowserGate(allJourneys, import.meta.url);
