from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum, IntFlag
from typing import Callable, Sequence

SNAPSHOT_SCHEMA_VERSION = 11


class AdapterResourceTier(IntEnum):
    VRAM = 0
    PHYSICAL_MEMORY = 1
    VIRTUAL_MEMORY = 2


class AdapterResourceKind(IntEnum):
    PRIMARY_DATA = 0
    CACHE = 1
    INDEX = 2
    MODEL_WEIGHTS = 3
    MEDIA_RESOURCE = 4
    EDITING_DOCUMENT_STATE = 5
    STAGING_BUFFER = 6
    TEMPORARY_COMPUTE_MEMORY = 7
    RUNTIME_OVERHEAD = 8
    RENDER_SURFACE = 9
    TEXTURE = 10
    RENDER_BUFFER = 11
    COMPUTE_BUFFER = 12


class AdapterResourceRecoveryKind(IntEnum):
    DISK_COPY = 0
    BUILT_DATA = 1
    LIVE_STATE = 2


class AdapterResourceGranularity(IntEnum):
    FULLY_LOADED = 0
    PARTIAL_USABLE = 1
    NOT_APPLICABLE = 2


class AdapterResourceActionRoute(IntEnum):
    MANAGER_DIRECT = 0
    ADAPTER_HANDLER = 1


class AdapterSoftwareSurfaceState(IntEnum):
    FOREGROUND_FOCUSED = 0
    FOREGROUND_UNFOCUSED = 1
    BACKGROUND_WINDOW = 2
    TRAY_BACKGROUND = 3
    PURE_BACKGROUND = 4


class AdapterResourceActionMask(IntFlag):
    NONE = 0
    DISCARD = 1 << 0
    TRIM = 1 << 1
    MOVE_DOWN = 1 << 2
    MOVE_UP = 1 << 3


class AdapterResourceDemandMask(IntFlag):
    NONE = 0
    REQUIRED_NOW = 1 << 0
    READY_SOON = 1 << 1
    PRELOAD_EAGER = 1 << 2
    PRELOAD_OPPORTUNISTIC = 1 << 3


ALL_ACTIONS = (
    AdapterResourceActionMask.DISCARD
    | AdapterResourceActionMask.TRIM
    | AdapterResourceActionMask.MOVE_DOWN
    | AdapterResourceActionMask.MOVE_UP
)
ALL_FRONTEND_DEMAND = (
    AdapterResourceDemandMask.REQUIRED_NOW
    | AdapterResourceDemandMask.READY_SOON
    | AdapterResourceDemandMask.PRELOAD_EAGER
    | AdapterResourceDemandMask.PRELOAD_OPPORTUNISTIC
)
MAX_RESOURCE_KIND = AdapterResourceKind.COMPUTE_BUFFER
MAX_ACTION_ROUTE = AdapterResourceActionRoute.ADAPTER_HANDLER


class AdapterResourceActionStatus(IntEnum):
    COMPLETED = 0
    RESOURCE_NOT_FOUND = 1
    ACTION_NOT_SUPPORTED = 2
    INVALID_REQUEST = 3
    RESOURCE_BUSY = 4
    FAILED = 5


@dataclass(frozen=True, slots=True)
class TieredResourceEntry:
    resource_key: int
    resource_id: int
    size_bytes: int
    tier: AdapterResourceTier
    resource_kind: AdapterResourceKind
    recovery_kind: AdapterResourceRecoveryKind
    granularity: AdapterResourceGranularity
    inapplicable_actions: AdapterResourceActionMask
    action_route: AdapterResourceActionRoute
    activity_score: int
    frontend_demand_mask: AdapterResourceDemandMask


@dataclass(frozen=True, slots=True)
class AdapterResourceSnapshot:
    schema_version: int
    application_id: str
    application_name: str
    process_id: int
    surface_state: AdapterSoftwareSurfaceState
    sequence: int
    captured_at: datetime
    resources: Sequence[TieredResourceEntry]


@dataclass(frozen=True, slots=True)
class AdapterResourceActionRequest:
    request_id: int
    resource_key: int
    action: AdapterResourceActionMask
    flags: int = 0


@dataclass(frozen=True, slots=True)
class AdapterResourceActionResult:
    request_id: int
    resource_key: int
    action: AdapterResourceActionMask
    status: AdapterResourceActionStatus
    previous_tier: AdapterResourceTier
    current_tier: AdapterResourceTier
    released_bytes: int
    resident_bytes: int
    detail_code: int = 0


AdapterResourceActionHandler = Callable[[AdapterResourceActionRequest], AdapterResourceActionResult]


def completed_action_result(
    request: AdapterResourceActionRequest,
    previous_tier: AdapterResourceTier,
    current_tier: AdapterResourceTier,
    released_bytes: int,
    resident_bytes: int,
    detail_code: int = 0,
) -> AdapterResourceActionResult:
    return AdapterResourceActionResult(
        request.request_id,
        request.resource_key,
        request.action,
        AdapterResourceActionStatus.COMPLETED,
        previous_tier,
        current_tier,
        released_bytes,
        resident_bytes,
        detail_code,
    )


def action_error_result(
    request: AdapterResourceActionRequest,
    status: AdapterResourceActionStatus,
    detail_code: int = 0,
) -> AdapterResourceActionResult:
    return AdapterResourceActionResult(
        request.request_id,
        request.resource_key,
        request.action,
        status,
        AdapterResourceTier.VRAM,
        AdapterResourceTier.VRAM,
        0,
        0,
        detail_code,
    )


class AdapterResourceActionDispatcher:
    def __init__(self) -> None:
        self._registrations: dict[int, tuple[AdapterResourceActionMask, AdapterResourceActionHandler]] = {}

    def register(
        self,
        resource_key: int,
        supported_actions: AdapterResourceActionMask,
        handler: AdapterResourceActionHandler,
    ) -> None:
        if resource_key == 0:
            raise ValueError("resource_key must be nonzero.")
        if supported_actions == AdapterResourceActionMask.NONE or int(supported_actions) & ~int(ALL_ACTIONS):
            raise ValueError("supported_actions contains unknown bits.")
        self._registrations[resource_key] = (supported_actions, handler)

    def unregister(self, resource_key: int) -> bool:
        return self._registrations.pop(resource_key, None) is not None

    def execute(self, request: AdapterResourceActionRequest) -> AdapterResourceActionResult:
        if request.resource_key == 0 or not _is_single_known_action(request.action):
            return action_error_result(request, AdapterResourceActionStatus.INVALID_REQUEST)
        registration = self._registrations.get(request.resource_key)
        if registration is None:
            return action_error_result(request, AdapterResourceActionStatus.RESOURCE_NOT_FOUND)
        supported_actions, handler = registration
        if not supported_actions & request.action:
            return action_error_result(request, AdapterResourceActionStatus.ACTION_NOT_SUPPORTED)
        return handler(request)


def adapter_resource_key(stable_id: str) -> int:
    stable_id = stable_id.strip()
    if not stable_id:
        raise ValueError("Stable id is required.")

    value = 14695981039346656037
    for byte in stable_id.encode("utf-8"):
        value ^= byte
        value = (value * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return value or 14695981039346656037


def validate_tiered_resource_entry(entry: TieredResourceEntry) -> None:
    if entry.resource_key == 0:
        raise ValueError("Adapter resource entry requires a nonzero resource_key.")
    if entry.resource_id <= 0 or entry.resource_id > 0xFFFFFFFF:
        raise ValueError("Adapter resource entry requires a nonzero uint32 resource_id.")
    if int(entry.resource_kind) < 0 or int(entry.resource_kind) > int(MAX_RESOURCE_KIND):
        raise ValueError("resource_kind is outside the known byte enum range.")
    if int(entry.inapplicable_actions) & ~int(ALL_ACTIONS):
        raise ValueError("inapplicable_actions contains unknown bits.")
    if int(entry.action_route) < 0 or int(entry.action_route) > int(MAX_ACTION_ROUTE):
        raise ValueError("action_route is outside the known byte enum range.")
    if entry.activity_score < 0 or entry.activity_score > 255:
        raise ValueError("activity_score must be a uint8 value.")
    if int(entry.frontend_demand_mask) & ~int(ALL_FRONTEND_DEMAND):
        raise ValueError("frontend_demand_mask contains unknown bits.")


def _is_single_known_action(action: AdapterResourceActionMask) -> bool:
    value = int(action)
    return value != 0 and value & (value - 1) == 0 and value & ~int(ALL_ACTIONS) == 0
