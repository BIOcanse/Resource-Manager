package resource_manager_adapter

import (
	"errors"
	"strings"
	"sync"
	"sync/atomic"
)

const SnapshotSchemaVersion uint8 = 11

type AdapterResourceTier uint8

const (
	TierVram AdapterResourceTier = iota
	TierPhysicalMemory
	TierVirtualMemory
)

type AdapterResourceKind uint8

const (
	ResourceKindPrimaryData AdapterResourceKind = iota
	ResourceKindCache
	ResourceKindIndex
	ResourceKindModelWeights
	ResourceKindMediaResource
	ResourceKindEditingDocumentState
	ResourceKindStagingBuffer
	ResourceKindTemporaryComputeMemory
	ResourceKindRuntimeOverhead
	ResourceKindRenderSurface
	ResourceKindTexture
	ResourceKindRenderBuffer
	ResourceKindComputeBuffer
	ResourceKindMax = ResourceKindComputeBuffer
)

type AdapterResourceRecoveryKind uint8

const (
	RecoveryDiskCopy AdapterResourceRecoveryKind = iota
	RecoveryBuiltData
	RecoveryLiveState
)

type AdapterResourceGranularity uint8

const (
	GranularityFullyLoaded AdapterResourceGranularity = iota
	GranularityPartialUsable
	GranularityNotApplicable
)

type AdapterResourceActionRoute uint8

const (
	ActionRouteManagerDirect  AdapterResourceActionRoute = 0
	ActionRouteAdapterHandler AdapterResourceActionRoute = 1
	ActionRouteMax                                       = ActionRouteAdapterHandler
)

type AdapterSoftwareSurfaceState uint8

const (
	SurfaceForegroundFocused AdapterSoftwareSurfaceState = iota
	SurfaceForegroundUnfocused
	SurfaceBackgroundWindow
	SurfaceTrayBackground
	SurfacePureBackground
)

type AdapterResourceActionMask uint8

const (
	ActionNone     AdapterResourceActionMask = 0
	ActionDiscard  AdapterResourceActionMask = 1 << 0
	ActionTrim     AdapterResourceActionMask = 1 << 1
	ActionMoveDown AdapterResourceActionMask = 1 << 2
	ActionMoveUp   AdapterResourceActionMask = 1 << 3
	ActionAll                                = ActionDiscard | ActionTrim | ActionMoveDown | ActionMoveUp
)

type AdapterResourceDemandMask uint8

const (
	DemandNone                 AdapterResourceDemandMask = 0
	DemandRequiredNow          AdapterResourceDemandMask = 1 << 0
	DemandReadySoon            AdapterResourceDemandMask = 1 << 1
	DemandPreloadEager         AdapterResourceDemandMask = 1 << 2
	DemandPreloadOpportunistic AdapterResourceDemandMask = 1 << 3
	DemandAll                                            = DemandRequiredNow | DemandReadySoon | DemandPreloadEager | DemandPreloadOpportunistic
)

type AdapterResourceActionStatus uint8

const (
	ActionStatusCompleted AdapterResourceActionStatus = iota
	ActionStatusResourceNotFound
	ActionStatusActionNotSupported
	ActionStatusInvalidRequest
	ActionStatusResourceBusy
	ActionStatusFailed
)

type AdapterResourceActionRequest struct {
	RequestID   uint64
	ResourceKey uint64
	Action      AdapterResourceActionMask
	Flags       uint8
}

type AdapterResourceActionResult struct {
	RequestID     uint64
	ResourceKey   uint64
	Action        AdapterResourceActionMask
	Status        AdapterResourceActionStatus
	PreviousTier  AdapterResourceTier
	CurrentTier   AdapterResourceTier
	ReleasedBytes uint64
	ResidentBytes uint64
	DetailCode    uint16
}

func CompletedActionResult(
	request AdapterResourceActionRequest,
	previousTier AdapterResourceTier,
	currentTier AdapterResourceTier,
	releasedBytes uint64,
	residentBytes uint64,
	detailCode uint16,
) AdapterResourceActionResult {
	return AdapterResourceActionResult{
		RequestID:     request.RequestID,
		ResourceKey:   request.ResourceKey,
		Action:        request.Action,
		Status:        ActionStatusCompleted,
		PreviousTier:  previousTier,
		CurrentTier:   currentTier,
		ReleasedBytes: releasedBytes,
		ResidentBytes: residentBytes,
		DetailCode:    detailCode,
	}
}

func ActionErrorResult(
	request AdapterResourceActionRequest,
	status AdapterResourceActionStatus,
	detailCode uint16,
) AdapterResourceActionResult {
	return AdapterResourceActionResult{
		RequestID:   request.RequestID,
		ResourceKey: request.ResourceKey,
		Action:      request.Action,
		Status:      status,
		DetailCode:  detailCode,
	}
}

type AdapterResourceActionHandler func(AdapterResourceActionRequest) AdapterResourceActionResult

type adapterResourceActionRegistration struct {
	supportedActions AdapterResourceActionMask
	handler          AdapterResourceActionHandler
}

type AdapterResourceActionDispatcher struct {
	mu            sync.Mutex
	registrations atomic.Value
}

func NewAdapterResourceActionDispatcher() *AdapterResourceActionDispatcher {
	dispatcher := &AdapterResourceActionDispatcher{}
	dispatcher.registrations.Store(map[uint64]adapterResourceActionRegistration{})
	return dispatcher
}

func (dispatcher *AdapterResourceActionDispatcher) Register(
	resourceKey uint64,
	supportedActions AdapterResourceActionMask,
	handler AdapterResourceActionHandler,
) error {
	if resourceKey == 0 {
		return errors.New("resource key must be nonzero")
	}
	if supportedActions == ActionNone || supportedActions&^ActionAll != 0 {
		return errors.New("supported actions contains unknown bits")
	}
	if handler == nil {
		return errors.New("handler is required")
	}

	dispatcher.mu.Lock()
	defer dispatcher.mu.Unlock()

	current := dispatcher.snapshot()
	next := make(map[uint64]adapterResourceActionRegistration, len(current)+1)
	for key, registration := range current {
		next[key] = registration
	}
	next[resourceKey] = adapterResourceActionRegistration{
		supportedActions: supportedActions,
		handler:          handler,
	}
	dispatcher.registrations.Store(next)
	return nil
}

func (dispatcher *AdapterResourceActionDispatcher) Unregister(resourceKey uint64) bool {
	dispatcher.mu.Lock()
	defer dispatcher.mu.Unlock()

	current := dispatcher.snapshot()
	if _, ok := current[resourceKey]; !ok {
		return false
	}
	next := make(map[uint64]adapterResourceActionRegistration, len(current)-1)
	for key, registration := range current {
		if key != resourceKey {
			next[key] = registration
		}
	}
	dispatcher.registrations.Store(next)
	return true
}

func (dispatcher *AdapterResourceActionDispatcher) Execute(request AdapterResourceActionRequest) AdapterResourceActionResult {
	if request.ResourceKey == 0 || !isSingleKnownAction(request.Action) {
		return ActionErrorResult(request, ActionStatusInvalidRequest, 0)
	}
	registration, ok := dispatcher.snapshot()[request.ResourceKey]
	if !ok {
		return ActionErrorResult(request, ActionStatusResourceNotFound, 0)
	}
	if registration.supportedActions&request.Action == 0 {
		return ActionErrorResult(request, ActionStatusActionNotSupported, 0)
	}
	return registration.handler(request)
}

func (dispatcher *AdapterResourceActionDispatcher) snapshot() map[uint64]adapterResourceActionRegistration {
	current := dispatcher.registrations.Load()
	if current == nil {
		return map[uint64]adapterResourceActionRegistration{}
	}
	return current.(map[uint64]adapterResourceActionRegistration)
}

type TieredResourceEntry struct {
	ResourceKey        uint64
	ResourceID         uint32
	SizeBytes          uint64
	Tier               AdapterResourceTier
	ResourceKind       AdapterResourceKind
	RecoveryKind       AdapterResourceRecoveryKind
	Granularity        AdapterResourceGranularity
	InapplicableAction AdapterResourceActionMask
	ActionRoute        AdapterResourceActionRoute
	ActivityScore      uint8
	FrontendDemandMask AdapterResourceDemandMask
}

func AdapterResourceKey(stableID string) (uint64, error) {
	stableID = strings.TrimSpace(stableID)
	if stableID == "" {
		return 0, errors.New("stable id is required")
	}

	var hash uint64 = 14695981039346656037
	for _, value := range []byte(stableID) {
		hash ^= uint64(value)
		hash *= 1099511628211
	}
	if hash == 0 {
		return 14695981039346656037, nil
	}
	return hash, nil
}

func ValidateTieredResourceEntry(entry TieredResourceEntry) error {
	if entry.ResourceKey == 0 {
		return errors.New("adapter resource entry requires a nonzero resource key")
	}
	if entry.ResourceID == 0 {
		return errors.New("adapter resource entry requires a nonzero resource id")
	}
	if entry.ResourceKind > ResourceKindMax {
		return errors.New("resource kind is outside the known byte enum range")
	}
	if entry.InapplicableAction&^ActionAll != 0 {
		return errors.New("inapplicable actions contains unknown bits")
	}
	if entry.ActionRoute > ActionRouteMax {
		return errors.New("action route is outside the known byte enum range")
	}
	if entry.FrontendDemandMask&^DemandAll != 0 {
		return errors.New("frontend demand mask contains unknown bits")
	}
	return nil
}

func isSingleKnownAction(action AdapterResourceActionMask) bool {
	value := uint8(action)
	return value != 0 && value&(value-1) == 0 && action&^ActionAll == 0
}
