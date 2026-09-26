package resource_manager_adapter

import "testing"

func TestStableKeyHashIsDeterministic(t *testing.T) {
	first, err := AdapterResourceKey("physical-memory.sample")
	if err != nil {
		t.Fatal(err)
	}
	second, err := AdapterResourceKey("physical-memory.sample")
	if err != nil {
		t.Fatal(err)
	}
	if first == 0 || first != second {
		t.Fatalf("unexpected stable key values: %d %d", first, second)
	}
}

func TestValidateTieredResourceEntry(t *testing.T) {
	resource, err := AdapterResourceKey("physical-memory.sample-cache")
	if err != nil {
		t.Fatal(err)
	}
	entry := TieredResourceEntry{
		ResourceKey:        resource,
		ResourceID:         1,
		SizeBytes:          4096,
		Tier:               TierPhysicalMemory,
		ResourceKind:       ResourceKindCache,
		RecoveryKind:       RecoveryDiskCopy,
		Granularity:        GranularityPartialUsable,
		InapplicableAction: ActionMoveUp,
		ActionRoute:        ActionRouteAdapterHandler,
		ActivityScore:      0,
		FrontendDemandMask: DemandRequiredNow,
	}
	if err := ValidateTieredResourceEntry(entry); err != nil {
		t.Fatal(err)
	}
}

func TestValidateTieredResourceEntryRejectsUnknownActionRoute(t *testing.T) {
	resource, err := AdapterResourceKey("physical-memory.sample-cache")
	if err != nil {
		t.Fatal(err)
	}
	entry := TieredResourceEntry{
		ResourceKey:  resource,
		ResourceID:   1,
		SizeBytes:    4096,
		Tier:         TierPhysicalMemory,
		ResourceKind: ResourceKindCache,
		RecoveryKind: RecoveryDiskCopy,
		Granularity:  GranularityPartialUsable,
		ActionRoute:  AdapterResourceActionRoute(0x80),
	}
	if err := ValidateTieredResourceEntry(entry); err == nil {
		t.Fatal("expected unknown action route to be rejected")
	}
}

func TestActionDispatcherExecutesRegisteredHandler(t *testing.T) {
	resource, err := AdapterResourceKey("physical-memory.sample-cache")
	if err != nil {
		t.Fatal(err)
	}
	dispatcher := NewAdapterResourceActionDispatcher()
	calls := 0
	if err := dispatcher.Register(resource, ActionDiscard|ActionTrim, func(request AdapterResourceActionRequest) AdapterResourceActionResult {
		calls++
		return CompletedActionResult(request, TierPhysicalMemory, TierVirtualMemory, 4096, 0, 0)
	}); err != nil {
		t.Fatal(err)
	}

	result := dispatcher.Execute(AdapterResourceActionRequest{
		RequestID:   0,
		ResourceKey: resource,
		Action:      ActionDiscard,
	})

	if result.RequestID != 0 {
		t.Fatalf("request id must not be normalized or validated: %+v", result)
	}
	if calls != 1 {
		t.Fatalf("expected one handler call, got %d", calls)
	}
	if result.Status != ActionStatusCompleted || result.ReleasedBytes != 4096 {
		t.Fatalf("unexpected action result: %+v", result)
	}
}

func TestActionDispatcherRejectsUnsupportedAndCompoundActions(t *testing.T) {
	resource, err := AdapterResourceKey("physical-memory.sample-cache")
	if err != nil {
		t.Fatal(err)
	}
	dispatcher := NewAdapterResourceActionDispatcher()
	calls := 0
	if err := dispatcher.Register(resource, ActionTrim, func(request AdapterResourceActionRequest) AdapterResourceActionResult {
		calls++
		return CompletedActionResult(request, TierPhysicalMemory, TierPhysicalMemory, 0, 1024, 0)
	}); err != nil {
		t.Fatal(err)
	}

	unsupported := dispatcher.Execute(AdapterResourceActionRequest{
		RequestID:   8,
		ResourceKey: resource,
		Action:      ActionMoveUp,
	})
	compound := dispatcher.Execute(AdapterResourceActionRequest{
		RequestID:   9,
		ResourceKey: resource,
		Action:      ActionTrim | ActionDiscard,
	})

	if unsupported.Status != ActionStatusActionNotSupported {
		t.Fatalf("unexpected unsupported status: %+v", unsupported)
	}
	if compound.Status != ActionStatusInvalidRequest {
		t.Fatalf("unexpected compound status: %+v", compound)
	}
	if calls != 0 {
		t.Fatalf("handler should not be called, got %d", calls)
	}
}
