using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Infrastructure.Paths;
using static ResourceManager.App.Infrastructure.DeviceActions.DeviceActionNativeBridge;

namespace ResourceManager.App.Infrastructure.DeviceActions;

// HTTP/managed-host adapter for the native finite operation. It owns no background
// work, retry, device selection policy or automatic action path.
internal sealed class DeviceActionBridge(
    IEnumerable<IDeviceRestartReportBridge> reports,
    IDeviceRestartPlatformBridge platform,
    IAppSettingsStore settings,
    INotificationOwner notifications,
    RuntimePersistenceCapabilityPolicy persistence,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<DeviceActionBridge> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeviceActionStoreBridge Store => new(Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "DeviceActions", "actions.db"));

    internal async Task<DeviceActionReply> ExecuteAsync(string reportEntryId, bool confirmed, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!persistence.MutablePersistenceEnabled) return new(7, 0, 0, null);
            var config = (await settings.LoadReadOnlyAsync(cancellationToken)).Settings.SystemIntegration.DeviceRestart;
            DeviceRestartReportTarget? target = null;
            // Adapters only read already-published/persisted report facts.
            using var adapters = reports.GetEnumerator();
            while (adapters.MoveNext() && target is null)
                target = await adapters.Current.ReadAsync(reportEntryId, cancellationToken);
            using var db = OpenSettledStore();
            var context = new Invocation(target, platform, db, notifications, logger);
            return Invoke(context, reportEntryId, confirmed, config.RecordCapacity, DeviceActionStoreBridge.Count(db), time.GetUtcNow());
        }
        finally { gate.Release(); }
    }

    private static unsafe DeviceActionReply Invoke(Invocation invocation, string id, bool confirmed, int capacity, uint count, DateTimeOffset now)
    {
        Text reportId;
        try { reportId = Text.Of(id); }
        catch (ArgumentException) { return new(2, 0, 0, null); }
        var handle = GCHandle.Alloc(invocation);
        try
        {
            var callbacks = new Callbacks { Context = GCHandle.ToIntPtr(handle), Lookup = &Lookup,
                Resolve = &Resolve, Restart = &Restart, Store = &Write, Notify = &Notify };
            Execute(in callbacks, in reportId, confirmed ? 1U : 0U, capacity, count, now.ToUnixTimeMilliseconds(), out var output);
            return new(output.Code, output.DeviceResult, output.Detail, output.Sequence > 0 ? output.Sequence : null);
        }
        finally { handle.Free(); }
    }

    internal async Task<DeviceActionHistory> ReadAsync(long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        if (ValidateCapacity(limit) != 0 || beforeSequence is <= 0) throw new ArgumentException("Invalid device history page.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!persistence.MutablePersistenceEnabled) return new([]);
            using var db = OpenSettledStore();
            return DeviceActionStoreBridge.Read(db, beforeSequence ?? long.MaxValue, limit);
        }
        finally { gate.Release(); }
    }
    internal async Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!persistence.MutablePersistenceEnabled) return 0;
            using var db = OpenSettledStore();
            return DeviceActionStoreBridge.ClearHistory(db);
        }
        finally { gate.Release(); }
    }

    private sealed record Invocation(DeviceRestartReportTarget? Target, IDeviceRestartPlatformBridge Platform,
        Microsoft.Data.Sqlite.SqliteConnection Database, INotificationOwner Notifications, ILogger Logger);

    // All callers hold gate, so a reservation found here has no executing request.
    // Recovery writes the native indeterminate outcome to history; it never touches a device.
    private Microsoft.Data.Sqlite.SqliteConnection OpenSettledStore()
    {
        var db = Store.Open();
        try { _ = DeviceActionStoreBridge.RecoverIncomplete(db); return db; }
        catch { db.Dispose(); throw; }
    }
    private static Invocation Context(nint value) => (Invocation)GCHandle.FromIntPtr(value).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Lookup(nint context, Text* _, Target* target)
    {
        try
        {
            var input = Context(context).Target;
            if (input is null) return 1;
            *target = new() { ReportType = Text.Of(input.ReportType), Module = Text.Of(input.DriverModule), InstanceId = Text.Of(input.DeviceInstanceId) };
            return 0;
        }
        catch (Exception failure) { Context(context).Logger.LogWarning(failure, "Device report adaptation failed."); return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Resolve(nint context, Text* module, Text* id, uint* detail)
    {
        try { var code = Context(context).Platform.Resolve(module->ToString(), out var value, out *detail); *id = Text.Of(value); return code; }
        catch (Exception failure) { Context(context).Logger.LogWarning(failure, "Device resolution bridge failed."); return 15; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Restart(nint context, Text* id, uint* detail)
    {
        try { return Context(context).Platform.Restart(id->ToString(), out *detail); }
        catch (Exception failure) { Context(context).Logger.LogWarning(failure, "Device restart bridge failed."); return 15; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Write(nint context, Record* record)
    {
        try { DeviceActionStoreBridge.Store(Context(context).Database, ref *record); return 0; }
        catch (Exception failure) { Context(context).Logger.LogError(failure, "Device action persistence failed; a reserved action is never replayed."); return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Notify(nint context, Record* record)
    {
        try
        {
            Context(context).Notifications.Post(NotificationKinds.DeviceRestartAction.Name, NotificationFacts.Of(
                new DeviceRestartNotificationFacts(record->Sequence, record->ReportId.ToString(), record->InstanceId.ToString(), record->ResultCode, record->Detail)));
            return 0;
        }
        catch (Exception failure) { Context(context).Logger.LogWarning(failure, "Device action notification failed."); return 1; }
    }
    public void Dispose() => gate.Dispose();
}
