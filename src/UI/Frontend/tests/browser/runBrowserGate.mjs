import managementSourceStatesJourney from "./managementSourceStatesJourney.js";
import fanManagementModesJourney from "./fanManagementModesJourney.js";
import frontendRefreshIntervalsJourney from "./frontendRefreshIntervalsJourney.js";
import productUpdatesJourney from "./productUpdatesJourney.js";
import updateNoticeJourney from "./updateNoticeJourney.js";
import managementInventoryRevisionJourney from "./managementInventoryRevisionJourney.js";
import diskUsageReadRecoveryJourney from "./diskUsageReadRecoveryJourney.js";
import diskUsageLocalTreemapJourney from "./diskUsageLocalTreemapJourney.js";
import diskUsagePagedTreeJourney from "./diskUsagePagedTreeJourney.js";
import diskUsageActionErrorsJourney from "./diskUsageActionErrorsJourney.js";
import diskUsageSetupOrderJourney from "./diskUsageSetupOrderJourney.js";
import overlaySettingsJourney from "./overlaySettingsJourney.js";
import loginStartupJourney from "./loginStartupJourney.js";
import managementFeedbackJourney from "./managementFeedbackJourney.js";
import reportRuleSettingsJourney from "./reportRuleSettingsJourney.js";
import trustedAccessJourney from "./trustedAccessJourney.js";
import trustedAccessRecoveryJourney from "./trustedAccessRecoveryJourney.js";
import softwareUnclassifiedJourney from "./softwareUnclassifiedJourney.js";
import frontendAuditJourney from "./frontendAuditJourney.js";
import stableButtonLabelsJourney from "./stableButtonLabelsJourney.js";
import { runBrowserGate } from "./browserGateRunner.mjs";
import cpuBaselineRatioJourney from "./cpuBaselineRatioJourney.js";
import cpuCurrentValueSubscriptionJourney from "./cpuCurrentValueSubscriptionJourney.js";
import cpuManualPlacementSubscriptionJourney from "./cpuManualPlacementSubscriptionJourney.js";
import creditsJourney from "./creditsJourney.js";
import currentValueSubscriptionJourney from "./currentValueSubscriptionJourney.js";
import monitorCurrentValueRetentionJourney from "./monitorCurrentValueRetentionJourney.js";
import deviceTopologySourceJourney from "./deviceTopologySourceJourney.js";
import diskUsageFeedbackRefreshJourney from "./diskUsageFeedbackRefreshJourney.js";
import diskUsageOperationInvalidationJourney from "./diskUsageOperationInvalidationJourney.js";
import frontendUsabilityJourney from "./frontendUsabilityJourney.js";
import frontendVisibilityDemandJourney from "./frontendVisibilityDemandJourney.js";
import optimizationReportsJourney from "./optimizationReportsJourney.js";
import r2RemediationJourney from "./r2RemediationJourney.js";
import resourceBarColorModesJourney from "./resourceBarColorModesJourney.js";
import resourceBarProportionJourney from "./resourceBarProportionJourney.js";
import controlReadingsJourney from "./controlReadingsJourney.js";
import controlCardJourney from "./controlCardJourney.js";
import controlAuditJourney from "./controlAuditJourney.js";
import controlNumberInitialJourney from "./controlNumberInitialJourney.js";
import controlSlowRequestJourney from "./controlSlowRequestJourney.js";
import controlToggleChoiceJourney from "./controlToggleChoiceJourney.js";
import controlSliderInteractionJourney from "./controlSliderInteractionJourney.js";
import metricFieldsJourney from "./metricFieldsJourney.js";
import metricFieldCardsJourney from "./metricFieldCardsJourney.js";
import metricPickerGpuNamesJourney from "./metricPickerGpuNamesJourney.js";
import gpuUnknownRuntimeJourney from "./gpuUnknownRuntimeJourney.js";
import gpuCommittedThresholdsJourney from "./gpuCommittedThresholdsJourney.js";
import smartDetailsPlacementFactsJourney from "./smartDetailsPlacementFactsJourney.js";
import performanceHistoryJourney from "./performanceHistoryJourney.js";
import resourceLayoutTaskJourney from "./resourceLayoutTaskJourney.js";
import resourceTableContentStateJourney from "./resourceTableContentStateJourney.js";
import resourceTableScrollStabilityJourney from "./resourceTableScrollStabilityJourney.js";
import resourceTableRowsJourney from "./resourceTableRowsJourney.js";
import columnResizeInteractionJourney from "./columnResizeInteractionJourney.js";
import fanCurveInteractionJourney from "./fanCurveInteractionJourney.js";
import switchInteractionJourney from "./switchInteractionJourney.js";
import reorderInteractionJourney from "./reorderInteractionJourney.js";
import motionFeelJourney from "./motionFeelJourney.js";
import pageMotionJourney from "./pageMotionJourney.js";
import settingsSourceJourney from "./settingsSourceJourney.js";
import aiGatewayKeysJourney from "./aiGatewayKeysJourney.js";
import gpuOverflowSettingsJourney from "./gpuOverflowSettingsJourney.js";
import realtimeActionBudgetSettingsJourney from "./realtimeActionBudgetSettingsJourney.js";
import serviceRecoverySettingsJourney from "./serviceRecoverySettingsJourney.js";
import softwareIssueJourney from "./softwareIssueJourney.js";
import softwareParticipationJourney from "./softwareParticipationJourney.js";
import taskCenterJourney from "./taskCenterJourney.js";
import taskCenterStatusIconJourney from "./taskCenterStatusIconJourney.js";
import managementSharedRefreshJourney from "./managementSharedRefreshJourney.js";
import targetedRecordingSourceJourney from "./targetedRecordingSourceJourney.js";
import themeConsistencyJourney from "./themeConsistencyJourney.js";
import pageTabsConsistencyJourney from "./pageTabsConsistencyJourney.js";
import uiPrimitivesJourney from "./uiPrimitivesJourney.js";
import localizationJourney from "./localizationJourney.js";
import accessibilitySemanticsJourney from "./accessibilitySemanticsJourney.js";

// 旅程登记表：[名称, (page, baseUrl, options) => 结果]。运行机制（工作进程、按语言拆分、输出和计时）在
// browserGateRunner.mjs，用法见那里的说明。
const allJourneys = [
  ["core-lease-contract", async (...args) => (await import("./coreLeaseContractJourney.js")).default(...args)],
  ["management-source-states", managementSourceStatesJourney],
  ["monitor-page-task", async (...args) => (await import("./monitorPageTaskJourney.js")).default(...args)],
  ["live-keyboard-focus", async (...args) => (await import("./liveKeyboardFocusJourney.js")).default(...args)],
  ["resource-table-totals-locale", async (...args) => (await import("./resourceTableTotalsLocaleJourney.js")).default(...args)],
  ["interface-number-locale", async (...args) => (await import("./interfaceNumberLocaleJourney.js")).default(...args)],
  ["automatic-animation", async (...args) => (await import("./automaticAnimationJourney.js")).default(...args)],
  ["dashboard-preferences", async (...args) => (await import("./dashboardPreferencesJourney.js")).default(...args)],
  ["service-recovery-settings", serviceRecoverySettingsJourney],
  ["product-updates", productUpdatesJourney],
  ["update-notice", updateNoticeJourney],
  ["language-load-failure", async (...args) => (await import("./languageLoadFailureJourney.js")).default(...args)],
  ["frontend-refresh-intervals", frontendRefreshIntervalsJourney],
  ["management-inventory-revisions", managementInventoryRevisionJourney],
  ["disk-usage-read-recovery", diskUsageReadRecoveryJourney],
  ["disk-usage-local-treemap", diskUsageLocalTreemapJourney],
  ["disk-usage-paged-tree", diskUsagePagedTreeJourney],
  ["disk-usage-action-errors", diskUsageActionErrorsJourney],
  ["disk-usage-setup-order", diskUsageSetupOrderJourney],
  ["overlay-settings", overlaySettingsJourney],
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
  ["ai-gateway-keys", aiGatewayKeysJourney],
  ["gpu-overflow-settings", gpuOverflowSettingsJourney],
  ["realtime-action-budget-settings", realtimeActionBudgetSettingsJourney],
  ["resource-bar-color-modes", resourceBarColorModesJourney],
  ["resource-bar-proportion", resourceBarProportionJourney],
  ["resource-bar-interaction", async (...args) => (await import("./resourceBarInteractionJourney.js")).default(...args)],
  ["resource-bar-real-cadence", async (...args) => (await import("./resourceBarRealCadenceJourney.js")).default(...args)],
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
  ["metric-catalog-push", async (...args) => (await import("./metricCatalogPushJourney.js")).default(...args)],
  ["metric-card-states", async (...args) => (await import("./metricCardStatesJourney.js")).default(...args)],
  ["metric-card-slots", async (...args) => (await import("./metricCardSlotsJourney.js")).default(...args)],
  ["metric-source-choice", async (...args) => (await import("./metricSourceChoiceJourney.js")).default(...args)],
  ["resource-table-source-choice", async (...args) => (await import("./resourceTableSourceChoiceJourney.js")).default(...args)],
  ["resource-table-column-editor", async (...args) => (await import("./resourceTableColumnEditorJourney.js")).default(...args)],
  ["physical-memory-residual", async (...args) => (await import("./physicalMemoryResidualJourney.js")).default(...args)],
  ["resource-table-totals", async (...args) => (await import("./resourceTableTotalsJourney.js")).default(...args)],
  ["resource-bar-source-choice", async (...args) => (await import("./resourceBarSourceChoiceJourney.js")).default(...args)],
  ["resource-bar-editor", async (...args) => (await import("./resourceBarEditorJourney.js")).default(...args)],
  ["overlay-source-choice", async (...args) => (await import("./overlaySourceChoiceJourney.js")).default(...args)],
  ["metric-picker-gpu-names", metricPickerGpuNamesJourney],
  ["gpu-unknown-runtime", gpuUnknownRuntimeJourney],
  ["gpu-committed-thresholds", gpuCommittedThresholdsJourney],
  ["gpu-dedicated-memory-states", async (...args) => (await import("./gpuDedicatedMemoryStatesJourney.js")).default(...args)],
  ["smart-details-placement-facts", smartDetailsPlacementFactsJourney],
  ["software-participation", softwareParticipationJourney],
  ["memory-protection-settings", async (...args) => (await import("./memoryProtectionSettingsJourney.js")).default(...args)],
  ["memory-protection-no-dump", async (...args) => (await import("./memoryProtectionNoDumpJourney.js")).default(...args)],
  ["memory-protection-layout", async (...args) => (await import("./memoryProtectionLayoutJourney.js")).default(...args)],
  ["performance-history", performanceHistoryJourney],
  ["theme-consistency", themeConsistencyJourney],
  ["page-tabs-consistency", pageTabsConsistencyJourney],
  ["software-issues", softwareIssueJourney],
  ["software-unclassified", softwareUnclassifiedJourney],
  ["device-topology-source", deviceTopologySourceJourney],
  ["disk-usage-feedback-refresh", diskUsageFeedbackRefreshJourney],
  ["disk-usage-operation-invalidation", diskUsageOperationInvalidationJourney],
  ["targeted-recording-source", targetedRecordingSourceJourney],
  ["targeted-recordings", async (...args) => (await import("./targetedRecordingsJourney.js")).default(...args)],
  ["cpu-baseline-ratio", cpuBaselineRatioJourney],
  ["cpu-current-value-subscription", cpuCurrentValueSubscriptionJourney],
  ["cpu-manual-placement-subscription", cpuManualPlacementSubscriptionJourney],
  ["current-value-subscription", currentValueSubscriptionJourney],
  ["monitor-current-value-retention", monitorCurrentValueRetentionJourney],
  ["frontend-visibility-demand", frontendVisibilityDemandJourney],
  ["optimization-reports", optimizationReportsJourney],
  ["task-center", taskCenterJourney],
  ["task-center-status-icon", taskCenterStatusIconJourney],
  ["resource-layout-task", resourceLayoutTaskJourney],
  ["resource-table-content-state", resourceTableContentStateJourney],
  ["resource-table-scroll-stability", resourceTableScrollStabilityJourney],
  ["resource-table-rows", resourceTableRowsJourney],
  ["column-resize-interaction", columnResizeInteractionJourney],
  ["fan-curve-interaction", fanCurveInteractionJourney],
  ["switch-interaction", switchInteractionJourney],
  ["wheel-accumulation", async (...args) => (await import("./wheelAccumulationJourney.js")).default(...args)],
  ["reorder-interaction", reorderInteractionJourney],
  ["motion-feel", motionFeelJourney],
  ["page-motion", pageMotionJourney],
  ["page-motion-timing", async (...args) => (await import("./pageMotionTimingJourney.js")).default(...args)],
  ["r2-remediation", r2RemediationJourney],
  ["accessibility-semantics", accessibilitySemanticsJourney]
];
await runBrowserGate(allJourneys, import.meta.url);
