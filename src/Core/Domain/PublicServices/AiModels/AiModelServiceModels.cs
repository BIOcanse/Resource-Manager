namespace ResourceManager.App.Domain.PublicServices.AiModels;

public sealed record AiModelRuntimeStatus(
    string Provider,
    string Endpoint,
    bool CliAvailable,
    bool ServerRunning,
    bool AutoStartEnabled,
    string? Message);

public sealed record AiModelLoadedInstance(
    string InstanceId,
    int? ContextLength);

public sealed record AiModelDescriptor(
    string Type,
    string Publisher,
    string Key,
    string DisplayName,
    string? Architecture,
    string? Format,
    string? Quantization,
    int? BitsPerWeight,
    long SizeBytes,
    string? Parameters,
    int? MaxContextLength,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<AiModelLoadedInstance> LoadedInstances);

public sealed record AiModelLoadRequest(
    string Model,
    int? ContextLength,
    int? EvalBatchSize,
    bool? FlashAttention,
    bool? OffloadKvCacheToGpu);

public sealed record AiModelLoadResult(
    string Type,
    string InstanceId,
    double LoadTimeSeconds,
    string Status);

public sealed record AiModelUnloadRequest(
    string InstanceId);

public sealed record AiModelUnloadResult(
    string InstanceId);
