namespace ResourceManager.Adapter;

public static class AdapterResourceProtocol
{
    public const byte SnapshotSchemaVersion = 11;

    public const int ResourceActivitySettlementSeconds = 5;

    public const byte ResourceActivityIncrement = 64;

    public const AdapterResourceActionMask AllActions =
        AdapterResourceActionMask.Discard
        | AdapterResourceActionMask.Trim
        | AdapterResourceActionMask.MoveDown
        | AdapterResourceActionMask.MoveUp;

    public const AdapterResourceDemandMask AllFrontendDemand =
        AdapterResourceDemandMask.RequiredNow
        | AdapterResourceDemandMask.ReadySoon
        | AdapterResourceDemandMask.PreloadEager
        | AdapterResourceDemandMask.PreloadOpportunistic;

    public const byte MaxActionRoute = (byte)AdapterResourceActionRoute.AdapterHandler;
}
