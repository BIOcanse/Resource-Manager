using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Notifications;

// Original access attestation stays in security; Zig selects by those attested
// access facts. H1/H2 call this binding until their native typed entry is wired.
internal static class NativeNotificationProjection
{
    internal static unsafe NotificationList Visible(NotificationList list, TrustedAccessAuthorizationContext? access)
    {
        var mask = access is null ? 0u :
            (access.Holds(AccessKeyScopes.ProcessesRead) ? 1u : 0u) |
            (access.Holds(AccessKeyScopes.Read) ? 2u : 0u) |
            (access.Holds(AccessKeyScopes.KeysManage) ? 4u : 0u);
        var call = new ProjectionCall(list.Notifications, null);
        var root = GCHandle.Alloc(call);
        try
        {
            var callbacks = new NativeNotificationMethods.Projection { ScopeAt = &ScopeAt, Select = &Select };
            var selected = NativeNotificationMethods.Project(GCHandle.ToIntPtr(root), (nuint)list.Notifications.Count, mask, in callbacks);
            call.Failure?.Throw();
            return selected == (nuint)list.Notifications.Count ? list : new(call.Selected);
        }
        finally { root.Free(); }
    }
    internal static unsafe bool SameSequences(IReadOnlyList<NotificationItem> left, IReadOnlyList<NotificationItem> right)
    {
        var call = new ProjectionCall(left, right);
        var root = GCHandle.Alloc(call);
        try
        {
            var equal = NativeNotificationMethods.SequencesEqual(GCHandle.ToIntPtr(root), (nuint)left.Count, (nuint)right.Count, &SequenceAt);
            call.Failure?.Throw();
            return equal != 0;
        }
        finally { root.Free(); }
    }
    private sealed class ProjectionCall(IReadOnlyList<NotificationItem> left, IReadOnlyList<NotificationItem>? right)
    {
        internal readonly IReadOnlyList<NotificationItem> Left = left;
        internal readonly IReadOnlyList<NotificationItem>? Right = right;
        internal readonly List<NotificationItem> Selected = [];
        internal ExceptionDispatchInfo? Failure;
    }
    private static ProjectionCall Borrow(nint context) => (ProjectionCall)GCHandle.FromIntPtr(context).Target!;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ScopeAt(nint context, nuint index)
    {
        var call = Borrow(context);
        try { return NativeNotificationMethods.Scope(call.Left[(int)index].Kind); }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Select(nint context, nuint index)
    {
        var call = Borrow(context);
        try { call.Selected.Add(call.Left[(int)index]); }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SequenceAt(nint context, uint side, nuint index)
    {
        var call = Borrow(context);
        try { return (side == 0 ? call.Left : call.Right!)[(int)index].Sequence; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 0; }
    }
}
