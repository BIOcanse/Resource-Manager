namespace ResourceManager.App.Domain.LocalSystem;

public sealed record LocalOnlineSearchRequest(string Query);

public sealed record LocalOnlineSearchResult(
    string Query,
    string Url);

public sealed record LocalPathPropertiesRequest(string Path);

public sealed record SystemProcessIdentity(
    int ProcessId,
    string ProcessStartKey);

public sealed record SystemProcessOperationRequest(
    IReadOnlyList<SystemProcessIdentity> Targets);

public sealed record SystemProcessOperationResult(
    SystemProcessAction Action,
    IReadOnlyList<SystemProcessOperationItem> Items,
    string? DirectoryPath = null);

public sealed record SystemProcessOperationItem(
    int ProcessId,
    string? ProcessName,
    string State,
    SystemProcessOutcome Outcome,
    string? Path = null,
    string? ProcessStartKey = null,
    int? NativeErrorCode = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SystemProcessAction>))]
public enum SystemProcessAction : byte { Terminate = 1, Dump = 2 }

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SystemProcessOutcome>))]
public enum SystemProcessOutcome : byte
{
    Terminated = 1,
    DumpCreated = 2,
    BackendProtected = 3,
    CriticalProcessProtected = 4,
    IdentityChanged = 5,
    ProcessExited = 6,
    AccessDenied = 7,
    NativeFailure = 8,
    FileFailure = 9,
    RequestRejected = 10
}
