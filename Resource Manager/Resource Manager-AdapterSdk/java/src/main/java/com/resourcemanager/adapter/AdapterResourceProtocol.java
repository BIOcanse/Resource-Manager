package com.resourcemanager.adapter;

public final class AdapterResourceProtocol {
    public static final byte SNAPSHOT_SCHEMA_VERSION = 11;

    public static final class Tier {
        public static final byte VRAM = 0;
        public static final byte PHYSICAL_MEMORY = 1;
        public static final byte VIRTUAL_MEMORY = 2;
        private Tier() {}
    }

    public static final class ResourceKind {
        public static final byte PRIMARY_DATA = 0;
        public static final byte CACHE = 1;
        public static final byte INDEX = 2;
        public static final byte MODEL_WEIGHTS = 3;
        public static final byte MEDIA_RESOURCE = 4;
        public static final byte EDITING_DOCUMENT_STATE = 5;
        public static final byte STAGING_BUFFER = 6;
        public static final byte TEMPORARY_COMPUTE_MEMORY = 7;
        public static final byte RUNTIME_OVERHEAD = 8;
        public static final byte RENDER_SURFACE = 9;
        public static final byte TEXTURE = 10;
        public static final byte RENDER_BUFFER = 11;
        public static final byte COMPUTE_BUFFER = 12;
        public static final byte MAX = COMPUTE_BUFFER;
        private ResourceKind() {}
    }

    public static final class RecoveryKind {
        public static final byte DISK_COPY = 0;
        public static final byte BUILT_DATA = 1;
        public static final byte LIVE_STATE = 2;
        private RecoveryKind() {}
    }

    public static final class Granularity {
        public static final byte FULLY_LOADED = 0;
        public static final byte PARTIAL_USABLE = 1;
        public static final byte NOT_APPLICABLE = 2;
        private Granularity() {}
    }

    public static final class ActionRoute {
        public static final byte MANAGER_DIRECT = 0;
        public static final byte ADAPTER_HANDLER = 1;
        public static final byte MAX = ADAPTER_HANDLER;
        private ActionRoute() {}
    }

    public static final class SurfaceState {
        public static final byte FOREGROUND_FOCUSED = 0;
        public static final byte FOREGROUND_UNFOCUSED = 1;
        public static final byte BACKGROUND_WINDOW = 2;
        public static final byte TRAY_BACKGROUND = 3;
        public static final byte PURE_BACKGROUND = 4;
        private SurfaceState() {}
    }

    public static final class ActionMask {
        public static final byte NONE = 0;
        public static final byte DISCARD = 1 << 0;
        public static final byte TRIM = 1 << 1;
        public static final byte MOVE_DOWN = 1 << 2;
        public static final byte MOVE_UP = 1 << 3;
        public static final byte ALL = DISCARD | TRIM | MOVE_DOWN | MOVE_UP;
        private ActionMask() {}
    }

    public static final class FrontendDemandMask {
        public static final byte NONE = 0;
        public static final byte REQUIRED_NOW = 1 << 0;
        public static final byte READY_SOON = 1 << 1;
        public static final byte PRELOAD_EAGER = 1 << 2;
        public static final byte PRELOAD_OPPORTUNISTIC = 1 << 3;
        public static final byte ALL = REQUIRED_NOW | READY_SOON | PRELOAD_EAGER | PRELOAD_OPPORTUNISTIC;
        private FrontendDemandMask() {}
    }

    public static final class ActionStatus {
        public static final byte COMPLETED = 0;
        public static final byte RESOURCE_NOT_FOUND = 1;
        public static final byte ACTION_NOT_SUPPORTED = 2;
        public static final byte INVALID_REQUEST = 3;
        public static final byte RESOURCE_BUSY = 4;
        public static final byte FAILED = 5;
        private ActionStatus() {}
    }

    public static final class TieredResourceEntry {
        public final long resourceKey;
        public final int resourceId;
        public final long sizeBytes;
        public final byte tier;
        public final byte resourceKind;
        public final byte recoveryKind;
        public final byte granularity;
        public final byte inapplicableActions;
        public final byte actionRoute;
        public final byte activityScore;
        public final byte frontendDemandMask;

        public TieredResourceEntry(
            long resourceKey,
            int resourceId,
            long sizeBytes,
            byte tier,
            byte resourceKind,
            byte recoveryKind,
            byte granularity,
            byte inapplicableActions,
            byte actionRoute,
            byte activityScore,
            byte frontendDemandMask) {
            this.resourceKey = resourceKey;
            this.resourceId = resourceId;
            this.sizeBytes = sizeBytes;
            this.tier = tier;
            this.resourceKind = resourceKind;
            this.recoveryKind = recoveryKind;
            this.granularity = granularity;
            this.inapplicableActions = inapplicableActions;
            this.actionRoute = actionRoute;
            this.activityScore = activityScore;
            this.frontendDemandMask = frontendDemandMask;
        }
    }

    public static final class ActionRequest {
        public final long requestId;
        public final long resourceKey;
        public final byte action;
        public final byte flags;

        public ActionRequest(long requestId, long resourceKey, byte action, byte flags) {
            this.requestId = requestId;
            this.resourceKey = resourceKey;
            this.action = action;
            this.flags = flags;
        }
    }

    public static final class ActionResult {
        public final long requestId;
        public final long resourceKey;
        public final byte action;
        public final byte status;
        public final byte previousTier;
        public final byte currentTier;
        public final long releasedBytes;
        public final long residentBytes;
        public final short detailCode;

        public ActionResult(
            long requestId,
            long resourceKey,
            byte action,
            byte status,
            byte previousTier,
            byte currentTier,
            long releasedBytes,
            long residentBytes,
            short detailCode) {
            this.requestId = requestId;
            this.resourceKey = resourceKey;
            this.action = action;
            this.status = status;
            this.previousTier = previousTier;
            this.currentTier = currentTier;
            this.releasedBytes = releasedBytes;
            this.residentBytes = residentBytes;
            this.detailCode = detailCode;
        }

        public static ActionResult completed(
            ActionRequest request,
            byte previousTier,
            byte currentTier,
            long releasedBytes,
            long residentBytes,
            short detailCode) {
            return new ActionResult(
                request.requestId,
                request.resourceKey,
                request.action,
                ActionStatus.COMPLETED,
                previousTier,
                currentTier,
                releasedBytes,
                residentBytes,
                detailCode);
        }

        public static ActionResult error(ActionRequest request, byte status, short detailCode) {
            return new ActionResult(
                request.requestId,
                request.resourceKey,
                request.action,
                status,
                Tier.VRAM,
                Tier.VRAM,
                0L,
                0L,
                detailCode);
        }
    }

    @FunctionalInterface
    public interface ActionHandler {
        ActionResult handle(ActionRequest request);
    }

    private static final class ActionRegistration {
        final byte supportedActions;
        final ActionHandler handler;

        ActionRegistration(byte supportedActions, ActionHandler handler) {
            this.supportedActions = supportedActions;
            this.handler = handler;
        }
    }

    public static final class ActionDispatcher {
        private final java.util.concurrent.ConcurrentHashMap<Long, ActionRegistration> registrations =
            new java.util.concurrent.ConcurrentHashMap<>();

        public void register(long resourceKey, byte supportedActions, ActionHandler handler) {
            if (resourceKey == 0L) {
                throw new IllegalArgumentException("Resource key must be nonzero.");
            }
            if (supportedActions == ActionMask.NONE || (((supportedActions & 0xff) & ~(ActionMask.ALL & 0xff)) != 0)) {
                throw new IllegalArgumentException("supportedActions contains unknown bits.");
            }
            if (handler == null) {
                throw new NullPointerException("handler");
            }
            registrations.put(resourceKey, new ActionRegistration(supportedActions, handler));
        }

        public boolean unregister(long resourceKey) {
            return registrations.remove(resourceKey) != null;
        }

        public ActionResult execute(ActionRequest request) {
            if (request.resourceKey == 0L || !isSingleKnownAction(request.action)) {
                return ActionResult.error(request, ActionStatus.INVALID_REQUEST, (short)0);
            }
            ActionRegistration registration = registrations.get(request.resourceKey);
            if (registration == null) {
                return ActionResult.error(request, ActionStatus.RESOURCE_NOT_FOUND, (short)0);
            }
            if (((registration.supportedActions & 0xff) & (request.action & 0xff)) == 0) {
                return ActionResult.error(request, ActionStatus.ACTION_NOT_SUPPORTED, (short)0);
            }
            return registration.handler.handle(request);
        }
    }

    private AdapterResourceProtocol() {}

    public static long adapterResourceKey(String stableId) {
        if (stableId == null || stableId.trim().isEmpty()) {
            throw new IllegalArgumentException("Stable id is required.");
        }

        long hash = -3750763034362895579L;
        byte[] bytes = stableId.trim().getBytes(java.nio.charset.StandardCharsets.UTF_8);
        for (byte value : bytes) {
            hash ^= value & 0xffL;
            hash *= 1099511628211L;
        }
        return hash == 0L ? -3750763034362895579L : hash;
    }

    public static void validateTieredResourceEntry(TieredResourceEntry entry) {
        if (entry.resourceKey == 0L) {
            throw new IllegalArgumentException("Adapter resource entry requires a nonzero resourceKey.");
        }
        if (entry.resourceId <= 0) {
            throw new IllegalArgumentException("Adapter resource entry requires a nonzero resourceId.");
        }
        if ((entry.resourceKind & 0xff) > (ResourceKind.MAX & 0xff)) {
            throw new IllegalArgumentException("resourceKind is outside the known byte enum range.");
        }
        if (((entry.inapplicableActions & 0xff) & ~(ActionMask.ALL & 0xff)) != 0) {
            throw new IllegalArgumentException("inapplicableActions contains unknown bits.");
        }
        if ((entry.actionRoute & 0xff) > (ActionRoute.MAX & 0xff)) {
            throw new IllegalArgumentException("actionRoute is outside the known byte enum range.");
        }
        if ((entry.activityScore & 0xff) > 255) {
            throw new IllegalArgumentException("activityScore is outside the uint8 range.");
        }
        if (((entry.frontendDemandMask & 0xff) & ~(FrontendDemandMask.ALL & 0xff)) != 0) {
            throw new IllegalArgumentException("frontendDemandMask contains unknown bits.");
        }
    }

    private static boolean isSingleKnownAction(byte action) {
        int value = action & 0xff;
        return value != 0 && (value & (value - 1)) == 0 && (value & ~(ActionMask.ALL & 0xff)) == 0;
    }
}
