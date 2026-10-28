namespace Resource_Manager_APP.Tests;

/// <summary>Explicit fixture declarations; no string condition catalog or production compatibility API.</summary>
internal static class TestStateUids
{
    internal const int Profile = 1001, Configured = 1002, Requested = 1003, Left = 1004, Right = 1005,
        BlockRule = 1006, BadRule = 1007, ParentProbe = 1008, Group = 1009, StartChild = 1010,
        Unfrozen = 1011, ExecutionEnabled = 1012;
    internal static readonly StateItemDeclaration[] Declarations =
    [
        Boolean(Profile), Boolean(Configured), Boolean(Requested), Boolean(Left), Boolean(Right),
        Boolean(BlockRule), Boolean(BadRule), Boolean(ParentProbe), Boolean(Group), Boolean(StartChild),
        Boolean(Unfrozen, perBlock: true), Boolean(ExecutionEnabled, perBlock: true)
    ];
    private static StateItemDeclaration Boolean(int uid, bool perBlock = false)
        => new(uid, StateItemStorage.Own, typeof(bool),
            new(StateItemOwnerKind.Component, ComponentLabel: "test fixture"), perBlock, StateItemTrigger.Event);
}
