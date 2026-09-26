package com.resourcemanager.adapter;

public final class AdapterResourceProtocolSelfTest {
    public static void main(String[] args) {
        long first = AdapterResourceProtocol.adapterResourceKey("physical-memory.sample");
        long second = AdapterResourceProtocol.adapterResourceKey("physical-memory.sample");
        if (first == 0L || first != second) {
            throw new AssertionError("stable key hash failed");
        }

        AdapterResourceProtocol.TieredResourceEntry entry =
            new AdapterResourceProtocol.TieredResourceEntry(
                AdapterResourceProtocol.adapterResourceKey("physical-memory.sample-cache"),
                1,
                4096L,
                AdapterResourceProtocol.Tier.PHYSICAL_MEMORY,
                AdapterResourceProtocol.ResourceKind.CACHE,
                AdapterResourceProtocol.RecoveryKind.DISK_COPY,
                AdapterResourceProtocol.Granularity.PARTIAL_USABLE,
                AdapterResourceProtocol.ActionMask.MOVE_UP,
                AdapterResourceProtocol.ActionRoute.ADAPTER_HANDLER,
                (byte)0,
                AdapterResourceProtocol.FrontendDemandMask.REQUIRED_NOW);

        AdapterResourceProtocol.validateTieredResourceEntry(entry);
        if (entry.resourceId != 1 || entry.frontendDemandMask != AdapterResourceProtocol.FrontendDemandMask.REQUIRED_NOW) {
            throw new AssertionError("resource fields not retained");
        }
        if (entry.actionRoute != AdapterResourceProtocol.ActionRoute.ADAPTER_HANDLER) {
            throw new AssertionError("action route not retained");
        }

        AdapterResourceProtocol.TieredResourceEntry invalidRoute =
            new AdapterResourceProtocol.TieredResourceEntry(
                AdapterResourceProtocol.adapterResourceKey("physical-memory.invalid-route"),
                1,
                4096L,
                AdapterResourceProtocol.Tier.PHYSICAL_MEMORY,
                AdapterResourceProtocol.ResourceKind.CACHE,
                AdapterResourceProtocol.RecoveryKind.DISK_COPY,
                AdapterResourceProtocol.Granularity.PARTIAL_USABLE,
                AdapterResourceProtocol.ActionMask.NONE,
                (byte)0x80,
                (byte)0,
                AdapterResourceProtocol.FrontendDemandMask.NONE);
        try {
            AdapterResourceProtocol.validateTieredResourceEntry(invalidRoute);
            throw new AssertionError("unknown action route should be rejected");
        } catch (IllegalArgumentException expected) {
            // expected
        }

        AdapterResourceProtocol.TieredResourceEntry invalidDemand =
            new AdapterResourceProtocol.TieredResourceEntry(
                AdapterResourceProtocol.adapterResourceKey("physical-memory.invalid-demand"),
                1,
                4096L,
                AdapterResourceProtocol.Tier.PHYSICAL_MEMORY,
                AdapterResourceProtocol.ResourceKind.CACHE,
                AdapterResourceProtocol.RecoveryKind.DISK_COPY,
                AdapterResourceProtocol.Granularity.PARTIAL_USABLE,
                AdapterResourceProtocol.ActionMask.NONE,
                AdapterResourceProtocol.ActionRoute.MANAGER_DIRECT,
                (byte)0,
                (byte)0x80);
        try {
            AdapterResourceProtocol.validateTieredResourceEntry(invalidDemand);
            throw new AssertionError("unknown frontend demand bits should be rejected");
        } catch (IllegalArgumentException expected) {
            // expected
        }

        AdapterResourceProtocol.ActionDispatcher dispatcher = new AdapterResourceProtocol.ActionDispatcher();
        long resource = AdapterResourceProtocol.adapterResourceKey("physical-memory.sample-cache");
        final int[] calls = new int[] { 0 };
        dispatcher.register(
            resource,
            (byte)(AdapterResourceProtocol.ActionMask.DISCARD | AdapterResourceProtocol.ActionMask.TRIM),
            request -> {
                calls[0]++;
                return AdapterResourceProtocol.ActionResult.completed(
                    request,
                    AdapterResourceProtocol.Tier.PHYSICAL_MEMORY,
                    AdapterResourceProtocol.Tier.VIRTUAL_MEMORY,
                    4096L,
                    0L,
                    (short)0);
            });

        AdapterResourceProtocol.ActionResult result = dispatcher.execute(
            new AdapterResourceProtocol.ActionRequest(
                0L,
                resource,
                AdapterResourceProtocol.ActionMask.DISCARD,
                (byte)0));
        if (result.requestId != 0L || calls[0] != 1 || result.status != AdapterResourceProtocol.ActionStatus.COMPLETED || result.releasedBytes != 4096L) {
            throw new AssertionError("action dispatcher failed");
        }

        AdapterResourceProtocol.ActionResult compound = dispatcher.execute(
            new AdapterResourceProtocol.ActionRequest(
                8L,
                resource,
                (byte)(AdapterResourceProtocol.ActionMask.DISCARD | AdapterResourceProtocol.ActionMask.TRIM),
                (byte)0));
        if (compound.status != AdapterResourceProtocol.ActionStatus.INVALID_REQUEST) {
            throw new AssertionError("compound action should be rejected");
        }
    }
}
