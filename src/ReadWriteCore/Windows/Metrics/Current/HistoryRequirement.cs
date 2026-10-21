namespace ResourceManager.App.Domain.Monitoring;

public readonly record struct HistoryRequirement(string DataItem, int RetainedRounds);
