using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

/// <summary>
/// 取组件安装器之外还需要的运行时文件（例如 PawnIO 的 RyzenSMU 模块）。
/// 已经在就什么都不做。
/// </summary>
public interface IDependencyPayloadAcquisition
{
    Task<DependencyPayloadResult> EnsureAsync(
        OptionalDependencyDefinition definition,
        CancellationToken cancellationToken);
}

public readonly record struct DependencyPayloadResult(bool Satisfied, string Message);
