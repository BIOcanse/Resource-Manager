using Microsoft.Data.Sqlite;
using static ResourceManager.App.Infrastructure.DeviceActions.DeviceActionNativeBridge;

namespace ResourceManager.App.Infrastructure.DeviceActions;

public sealed record DeviceActionRecord(long Sequence, string State, string ReportEntryId, string DeviceInstanceId,
    DateTimeOffset OccurredAt, uint ResultCode, uint Detail);
public sealed record DeviceActionHistory(IReadOnlyList<DeviceActionRecord> Records);
public sealed record DeviceActionReply(uint ReasonCode, uint DeviceResultCode, uint Detail, long? Sequence);

// Named storage adapter: native code supplies the identity, lifecycle and result.
// SQLite FULL synchronous commits reserve a slot before any device write. Sequence
// is never reused, including across history clear. No timers or retention deletion.
internal sealed class DeviceActionStoreBridge(string path)
{
    internal SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS device_actions (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, state INTEGER NOT NULL,
              report_id TEXT NOT NULL, device_id TEXT NOT NULL, occurred_at INTEGER NOT NULL,
              result_code INTEGER NOT NULL, detail INTEGER NOT NULL);
            """;
            command.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    internal static int RecoverIncomplete(SqliteConnection db)
    {
        Record interrupted = default;
        _ = RecoverRecord(ref interrupted);
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE device_actions SET state=$state,result_code=$result,detail=$detail WHERE state=0";
        command.Parameters.AddWithValue("$state", interrupted.State);
        command.Parameters.AddWithValue("$result", interrupted.ResultCode);
        command.Parameters.AddWithValue("$detail", interrupted.Detail);
        return command.ExecuteNonQuery();
    }
    internal static uint Count(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM device_actions";
        return checked((uint)(long)command.ExecuteScalar()!);
    }
    internal static void Store(SqliteConnection db, ref Record record)
    {
        using var command = db.CreateCommand();
        if (record.Sequence == 0)
        {
            command.CommandText = """
                INSERT INTO device_actions(state,report_id,device_id,occurred_at,result_code,detail)
                VALUES($state,$report,$device,$time,$result,$detail) RETURNING sequence
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE device_actions SET state=$state,result_code=$result,detail=$detail
                WHERE sequence=$sequence RETURNING sequence
                """;
            command.Parameters.AddWithValue("$sequence", record.Sequence);
        }
        command.Parameters.AddWithValue("$state", record.State);
        command.Parameters.AddWithValue("$report", record.ReportId.ToString());
        command.Parameters.AddWithValue("$device", record.InstanceId.ToString());
        command.Parameters.AddWithValue("$time", record.OccurredAt);
        command.Parameters.AddWithValue("$result", record.ResultCode);
        command.Parameters.AddWithValue("$detail", record.Detail);
        record.Sequence = (long)(command.ExecuteScalar() ?? throw new IOException("Device action reservation disappeared."));
    }
    internal static DeviceActionHistory Read(SqliteConnection db, long beforeSequence, int limit)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT sequence,state,report_id,device_id,occurred_at,result_code,detail FROM device_actions
            WHERE sequence < $before ORDER BY sequence DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", beforeSequence);
        command.Parameters.AddWithValue("$limit", limit);
        using var rows = command.ExecuteReader();
        var result = new List<DeviceActionRecord>();
        while (rows.Read()) result.Add(new(rows.GetInt64(0), rows.GetInt32(1) == 0 ? "active" : "history",
            rows.GetString(2), rows.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(4)),
            checked((uint)rows.GetInt64(5)), checked((uint)rows.GetInt64(6))));
        return new(result);
    }
    internal static int ClearHistory(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM device_actions WHERE state=1";
        return command.ExecuteNonQuery();
    }
}
