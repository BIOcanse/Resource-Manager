using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ResourceManager.Adapter.Tasks;

public static class BackgroundTaskStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceling = "canceling";
    public const string Cancelled = "cancelled";
    public const string Blocked = "blocked";
    public const string Paused = "paused";

    public static bool IsTerminal(string? status)
    {
        return string.Equals(status, Succeeded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsActive(string? status)
    {
        return string.Equals(status, Queued, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Canceling, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Blocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, Paused, StringComparison.OrdinalIgnoreCase);
    }
}

public static class BackgroundTaskConstants
{
    public const string DefaultSpaceId = "default";
    public const string DefaultQueueId = "default";
}

public class BackgroundTaskRecord
{
    public string TaskId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string QueueId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public string Status { get; set; } = BackgroundTaskStatuses.Queued;
    public JsonElement Payload { get; set; }
    public int? ProgressPercent { get; set; }
    public string Step { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public bool PauseRequested { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class BackgroundTaskLogEntry
{
    public string LogId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string QueueId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; set; }
}

public class BackgroundTaskSubmission
{
    public string Kind { get; set; } = string.Empty;
    public string QueueId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public object? Payload { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class BackgroundTaskSpaceDocument<TTask, TLog>
    where TTask : BackgroundTaskRecord
    where TLog : BackgroundTaskLogEntry
{
    public string SpaceId { get; set; } = BackgroundTaskConstants.DefaultSpaceId;
    public long Version { get; set; }
    public List<TTask> Tasks { get; set; } = new();
    public List<TLog> Logs { get; set; } = new();
}

public class BackgroundTaskSpaceDocument
    : BackgroundTaskSpaceDocument<BackgroundTaskRecord, BackgroundTaskLogEntry>
{
}

public class BackgroundQueueSnapshot
{
    public string QueueId { get; set; } = string.Empty;
    public int Queued { get; set; }
    public int Running { get; set; }
    public string CurrentTaskId { get; set; } = string.Empty;
    public DateTimeOffset? LastActivityAt { get; set; }
}

public class BackgroundTaskSummary
{
    public int Queued { get; set; }
    public int Running { get; set; }
    public int Canceling { get; set; }
    public int Paused { get; set; }
    public int Failed { get; set; }
    public int Succeeded { get; set; }
    public int Cancelled { get; set; }
}

public class BackgroundTaskSnapshot<TTask, TLog, TQueue, TSummary>
    where TTask : BackgroundTaskRecord
    where TLog : BackgroundTaskLogEntry
    where TQueue : BackgroundQueueSnapshot
    where TSummary : BackgroundTaskSummary, new()
{
    public string SpaceId { get; set; } = BackgroundTaskConstants.DefaultSpaceId;
    public long Version { get; set; }
    public List<TQueue> Queues { get; set; } = new();
    public List<TTask> ActiveTasks { get; set; } = new();
    public List<TLog> RecentLogs { get; set; } = new();
    public TSummary Summary { get; set; } = new();
}

public class BackgroundTaskSnapshot
    : BackgroundTaskSnapshot<BackgroundTaskRecord, BackgroundTaskLogEntry, BackgroundQueueSnapshot, BackgroundTaskSummary>
{
}

public static class BackgroundTaskContractValidator
{
    public static void EnsureValidSubmission(BackgroundTaskSubmission submission)
    {
        if (submission is null)
        {
            throw new ArgumentNullException(nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.Kind))
        {
            throw new InvalidOperationException("Task kind is required.");
        }

        if (string.IsNullOrWhiteSpace(submission.QueueId))
        {
            throw new InvalidOperationException("Task queue id is required.");
        }
    }

    public static void EnsureValidTaskSpace<TTask, TLog>(
        BackgroundTaskSpaceDocument<TTask, TLog> document)
        where TTask : BackgroundTaskRecord
        where TLog : BackgroundTaskLogEntry
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (string.IsNullOrWhiteSpace(document.SpaceId))
        {
            throw new InvalidOperationException("Task space id is required.");
        }

        if (document.Tasks is null)
        {
            throw new InvalidOperationException("Task list is required.");
        }

        if (document.Logs is null)
        {
            throw new InvalidOperationException("Task log list is required.");
        }
    }

    public static void EnsureValidQueueSnapshot(BackgroundQueueSnapshot queue)
    {
        if (queue is null)
        {
            throw new ArgumentNullException(nameof(queue));
        }

        if (string.IsNullOrWhiteSpace(queue.QueueId))
        {
            throw new InvalidOperationException("Task queue id is required.");
        }
    }
}
