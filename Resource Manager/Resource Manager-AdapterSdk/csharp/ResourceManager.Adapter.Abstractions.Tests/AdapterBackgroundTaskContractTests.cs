using ResourceManager.Adapter.Tasks;

public sealed class AdapterBackgroundTaskContractTests
{
    [Fact]
    public void Task_status_helpers_match_shared_task_contract()
    {
        Assert.True(BackgroundTaskStatuses.IsTerminal(BackgroundTaskStatuses.Succeeded));
        Assert.True(BackgroundTaskStatuses.IsTerminal(BackgroundTaskStatuses.Failed));
        Assert.True(BackgroundTaskStatuses.IsTerminal(BackgroundTaskStatuses.Cancelled));
        Assert.False(BackgroundTaskStatuses.IsTerminal(BackgroundTaskStatuses.Running));

        Assert.True(BackgroundTaskStatuses.IsActive(BackgroundTaskStatuses.Queued));
        Assert.True(BackgroundTaskStatuses.IsActive(BackgroundTaskStatuses.Paused));
        Assert.False(BackgroundTaskStatuses.IsActive(BackgroundTaskStatuses.Cancelled));
    }

    [Fact]
    public void Submission_validator_requires_kind_and_queue()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BackgroundTaskContractValidator.EnsureValidSubmission(new BackgroundTaskSubmission
            {
                QueueId = "store"
            }));

        Assert.Throws<InvalidOperationException>(() =>
            BackgroundTaskContractValidator.EnsureValidSubmission(new BackgroundTaskSubmission
            {
                Kind = "word-list.import"
            }));

        BackgroundTaskContractValidator.EnsureValidSubmission(new BackgroundTaskSubmission
        {
            Kind = "word-list.import",
            QueueId = "store"
        });
    }

    [Fact]
    public void Task_record_metadata_uses_case_insensitive_keys()
    {
        var task = new BackgroundTaskRecord();
        task.Metadata["ResultJson"] = "{}";

        Assert.True(task.Metadata.ContainsKey("resultjson"));
    }

    [Fact]
    public void Task_space_and_snapshot_contracts_cover_queue_views()
    {
        var space = new BackgroundTaskSpaceDocument
        {
            SpaceId = "sample:default",
            Version = 3
        };
        space.Tasks.Add(new BackgroundTaskRecord
        {
            TaskId = "task-1",
            Kind = "word-list.import",
            QueueId = "store:import"
        });
        space.Logs.Add(new BackgroundTaskLogEntry
        {
            LogId = "log-1",
            TaskId = "task-1",
            QueueId = "store:import"
        });

        BackgroundTaskContractValidator.EnsureValidTaskSpace(space);

        var snapshot = new BackgroundTaskSnapshot
        {
            SpaceId = space.SpaceId,
            Version = space.Version
        };
        snapshot.Queues.Add(new BackgroundQueueSnapshot
        {
            QueueId = "store:import",
            Queued = 1,
            LastActivityAt = DateTimeOffset.UnixEpoch
        });
        snapshot.ActiveTasks.Add(space.Tasks[0]);
        snapshot.RecentLogs.Add(space.Logs[0]);
        snapshot.Summary.Queued = 1;

        BackgroundTaskContractValidator.EnsureValidQueueSnapshot(snapshot.Queues[0]);
        Assert.Equal("store:import", snapshot.Queues[0].QueueId);
        Assert.Equal(1, snapshot.Summary.Queued);
    }

    [Fact]
    public void Generic_task_space_preserves_app_specific_record_types()
    {
        var space = new BackgroundTaskSpaceDocument<CustomTaskRecord, CustomTaskLogEntry>();
        var task = new CustomTaskRecord
        {
            TaskId = "custom-task",
            Kind = "custom.kind",
            QueueId = "custom:queue",
            CustomOwner = "adapter"
        };
        space.Tasks.Add(task);

        Assert.Same(task, space.Tasks[0]);
        Assert.Equal("adapter", space.Tasks[0].CustomOwner);
    }

    private sealed class CustomTaskRecord : BackgroundTaskRecord
    {
        public string CustomOwner { get; set; } = string.Empty;
    }

    private sealed class CustomTaskLogEntry : BackgroundTaskLogEntry
    {
    }
}
