use std::collections::HashMap;

pub const SNAPSHOT_SCHEMA_VERSION: u8 = 11;

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceTier {
    Vram = 0,
    PhysicalMemory = 1,
    VirtualMemory = 2,
}

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceKind {
    PrimaryData = 0,
    Cache = 1,
    Index = 2,
    ModelWeights = 3,
    MediaResource = 4,
    EditingDocumentState = 5,
    StagingBuffer = 6,
    TemporaryComputeMemory = 7,
    RuntimeOverhead = 8,
    RenderSurface = 9,
    Texture = 10,
    RenderBuffer = 11,
    ComputeBuffer = 12,
}

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceRecoveryKind {
    DiskCopy = 0,
    BuiltData = 1,
    LiveState = 2,
}

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceGranularity {
    FullyLoaded = 0,
    PartialUsable = 1,
    NotApplicable = 2,
}

pub mod action_route {
    pub const MANAGER_DIRECT: u8 = 0;
    pub const ADAPTER_HANDLER: u8 = 1;
    pub const MAX: u8 = ADAPTER_HANDLER;
}

pub mod surface_state {
    pub const FOREGROUND_FOCUSED: u8 = 0;
    pub const FOREGROUND_UNFOCUSED: u8 = 1;
    pub const BACKGROUND_WINDOW: u8 = 2;
    pub const TRAY_BACKGROUND: u8 = 3;
    pub const PURE_BACKGROUND: u8 = 4;
}

pub mod action_mask {
    pub const NONE: u8 = 0;
    pub const DISCARD: u8 = 1 << 0;
    pub const TRIM: u8 = 1 << 1;
    pub const MOVE_DOWN: u8 = 1 << 2;
    pub const MOVE_UP: u8 = 1 << 3;
    pub const ALL: u8 = DISCARD | TRIM | MOVE_DOWN | MOVE_UP;
}

pub mod frontend_demand_mask {
    pub const NONE: u8 = 0;
    pub const REQUIRED_NOW: u8 = 1 << 0;
    pub const READY_SOON: u8 = 1 << 1;
    pub const PRELOAD_EAGER: u8 = 1 << 2;
    pub const PRELOAD_OPPORTUNISTIC: u8 = 1 << 3;
    pub const ALL: u8 = REQUIRED_NOW | READY_SOON | PRELOAD_EAGER | PRELOAD_OPPORTUNISTIC;
}

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceActionStatus {
    Completed = 0,
    ResourceNotFound = 1,
    ActionNotSupported = 2,
    InvalidRequest = 3,
    ResourceBusy = 4,
    Failed = 5,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AdapterResourceActionRequest {
    pub request_id: u64,
    pub resource_key: u64,
    pub action: u8,
    pub flags: u8,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AdapterResourceActionResult {
    pub request_id: u64,
    pub resource_key: u64,
    pub action: u8,
    pub status: AdapterResourceActionStatus,
    pub previous_tier: AdapterResourceTier,
    pub current_tier: AdapterResourceTier,
    pub released_bytes: u64,
    pub resident_bytes: u64,
    pub detail_code: u16,
}

impl AdapterResourceActionResult {
    pub fn completed(
        request: &AdapterResourceActionRequest,
        previous_tier: AdapterResourceTier,
        current_tier: AdapterResourceTier,
        released_bytes: u64,
        resident_bytes: u64,
        detail_code: u16,
    ) -> Self {
        Self {
            request_id: request.request_id,
            resource_key: request.resource_key,
            action: request.action,
            status: AdapterResourceActionStatus::Completed,
            previous_tier,
            current_tier,
            released_bytes,
            resident_bytes,
            detail_code,
        }
    }

    pub fn error(
        request: &AdapterResourceActionRequest,
        status: AdapterResourceActionStatus,
        detail_code: u16,
    ) -> Self {
        Self {
            request_id: request.request_id,
            resource_key: request.resource_key,
            action: request.action,
            status,
            previous_tier: AdapterResourceTier::Vram,
            current_tier: AdapterResourceTier::Vram,
            released_bytes: 0,
            resident_bytes: 0,
            detail_code,
        }
    }
}

type AdapterResourceActionHandler =
    Box<dyn Fn(&AdapterResourceActionRequest) -> AdapterResourceActionResult + Send + Sync>;

struct AdapterResourceActionRegistration {
    supported_actions: u8,
    handler: AdapterResourceActionHandler,
}

#[derive(Default)]
pub struct AdapterResourceActionDispatcher {
    registrations: HashMap<u64, AdapterResourceActionRegistration>,
}

impl AdapterResourceActionDispatcher {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register<F>(
        &mut self,
        resource_key: u64,
        supported_actions: u8,
        handler: F,
    ) -> Result<(), AdapterResourceValidationError>
    where
        F: Fn(&AdapterResourceActionRequest) -> AdapterResourceActionResult + Send + Sync + 'static,
    {
        if resource_key == 0 {
            return Err(AdapterResourceValidationError::MissingResourceKey);
        }
        if supported_actions == action_mask::NONE || supported_actions & !action_mask::ALL != 0 {
            return Err(AdapterResourceValidationError::UnknownActionBits);
        }
        self.registrations.insert(
            resource_key,
            AdapterResourceActionRegistration {
                supported_actions,
                handler: Box::new(handler),
            },
        );
        Ok(())
    }

    pub fn unregister(&mut self, resource_key: u64) -> bool {
        self.registrations.remove(&resource_key).is_some()
    }

    pub fn execute(&self, request: &AdapterResourceActionRequest) -> AdapterResourceActionResult {
        if request.resource_key == 0 || !is_single_known_action(request.action) {
            return AdapterResourceActionResult::error(
                request,
                AdapterResourceActionStatus::InvalidRequest,
                0,
            );
        }
        let Some(registration) = self.registrations.get(&request.resource_key) else {
            return AdapterResourceActionResult::error(
                request,
                AdapterResourceActionStatus::ResourceNotFound,
                0,
            );
        };
        if registration.supported_actions & request.action == 0 {
            return AdapterResourceActionResult::error(
                request,
                AdapterResourceActionStatus::ActionNotSupported,
                0,
            );
        }
        (registration.handler)(request)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TieredResourceEntry {
    pub resource_key: u64,
    pub resource_id: u32,
    pub size_bytes: u64,
    pub tier: AdapterResourceTier,
    pub resource_kind: AdapterResourceKind,
    pub recovery_kind: AdapterResourceRecoveryKind,
    pub granularity: AdapterResourceGranularity,
    pub inapplicable_actions: u8,
    pub action_route: u8,
    pub activity_score: u8,
    pub frontend_demand_mask: u8,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterResourceValidationError {
    EmptyStableId,
    MissingResourceKey,
    MissingResourceId,
    UnknownActionBits,
    UnknownActionRoute,
    UnknownFrontendDemandBits,
}

fn is_single_known_action(action: u8) -> bool {
    action != 0 && action & (action - 1) == 0 && action & !action_mask::ALL == 0
}

pub fn adapter_resource_key(stable_id: &str) -> Result<u64, AdapterResourceValidationError> {
    let stable_id = stable_id.trim();
    if stable_id.is_empty() {
        return Err(AdapterResourceValidationError::EmptyStableId);
    }

    let mut hash: u64 = 14_695_981_039_346_656_037;
    for byte in stable_id.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(1_099_511_628_211);
    }

    Ok(if hash == 0 {
        14_695_981_039_346_656_037
    } else {
        hash
    })
}

pub fn validate_tiered_resource_entry(
    entry: &TieredResourceEntry,
) -> Result<(), AdapterResourceValidationError> {
    if entry.resource_key == 0 {
        return Err(AdapterResourceValidationError::MissingResourceKey);
    }
    if entry.resource_id == 0 {
        return Err(AdapterResourceValidationError::MissingResourceId);
    }
    if entry.inapplicable_actions & !action_mask::ALL != 0 {
        return Err(AdapterResourceValidationError::UnknownActionBits);
    }
    if entry.action_route > action_route::MAX {
        return Err(AdapterResourceValidationError::UnknownActionRoute);
    }
    if entry.frontend_demand_mask & !frontend_demand_mask::ALL != 0 {
        return Err(AdapterResourceValidationError::UnknownFrontendDemandBits);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stable_key_hash_is_deterministic() {
        let first = adapter_resource_key("physical-memory.sample").unwrap();
        let second = adapter_resource_key("physical-memory.sample").unwrap();
        assert_ne!(0, first);
        assert_eq!(first, second);
    }

    #[test]
    fn validator_accepts_entry() {
        let entry = TieredResourceEntry {
            resource_key: adapter_resource_key("physical-memory.sample-cache").unwrap(),
            resource_id: 1,
            size_bytes: 4096,
            tier: AdapterResourceTier::PhysicalMemory,
            resource_kind: AdapterResourceKind::Cache,
            recovery_kind: AdapterResourceRecoveryKind::DiskCopy,
            granularity: AdapterResourceGranularity::PartialUsable,
            inapplicable_actions: action_mask::MOVE_UP,
            action_route: action_route::ADAPTER_HANDLER,
            activity_score: 0,
            frontend_demand_mask: frontend_demand_mask::REQUIRED_NOW,
        };
        assert_eq!(frontend_demand_mask::REQUIRED_NOW, entry.frontend_demand_mask);
        assert!(validate_tiered_resource_entry(&entry).is_ok());
    }

    #[test]
    fn validator_rejects_unknown_action_route() {
        let entry = TieredResourceEntry {
            resource_key: adapter_resource_key("physical-memory.sample-cache").unwrap(),
            resource_id: 1,
            size_bytes: 4096,
            tier: AdapterResourceTier::PhysicalMemory,
            resource_kind: AdapterResourceKind::Cache,
            recovery_kind: AdapterResourceRecoveryKind::DiskCopy,
            granularity: AdapterResourceGranularity::PartialUsable,
            inapplicable_actions: action_mask::NONE,
            action_route: 0x80,
            activity_score: 0,
            frontend_demand_mask: frontend_demand_mask::NONE,
        };
        assert_eq!(
            Err(AdapterResourceValidationError::UnknownActionRoute),
            validate_tiered_resource_entry(&entry)
        );
    }

    #[test]
    fn action_dispatcher_executes_registered_handler() {
        let resource_key = adapter_resource_key("physical-memory.sample-cache").unwrap();
        let mut dispatcher = AdapterResourceActionDispatcher::new();
        dispatcher
            .register(
                resource_key,
                action_mask::DISCARD | action_mask::TRIM,
                |request| {
                    AdapterResourceActionResult::completed(
                        request,
                        AdapterResourceTier::PhysicalMemory,
                        AdapterResourceTier::VirtualMemory,
                        4096,
                        0,
                        0,
                    )
                },
            )
            .unwrap();

        let result = dispatcher.execute(&AdapterResourceActionRequest {
            request_id: 0,
            resource_key,
            action: action_mask::DISCARD,
            flags: 0,
        });

        assert_eq!(0, result.request_id);
        assert_eq!(AdapterResourceActionStatus::Completed, result.status);
        assert_eq!(4096, result.released_bytes);
    }

    #[test]
    fn action_dispatcher_rejects_compound_action() {
        let resource_key = adapter_resource_key("physical-memory.sample-cache").unwrap();
        let dispatcher = AdapterResourceActionDispatcher::new();
        let result = dispatcher.execute(&AdapterResourceActionRequest {
            request_id: 8,
            resource_key,
            action: action_mask::DISCARD | action_mask::TRIM,
            flags: 0,
        });

        assert_eq!(AdapterResourceActionStatus::InvalidRequest, result.status);
    }
}
