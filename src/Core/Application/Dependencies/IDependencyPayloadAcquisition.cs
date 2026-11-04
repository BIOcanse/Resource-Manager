using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

/// <summary>
/// 取组件安装器之外还需要的运行时文件（例如 PawnIO 的 RyzenSMU 模块）。
/// 文件齐全且发布记录符合当前固定发布时不修改文件；仅由显式安装/验证调用。
/// </summary>
public interface IDependencyPayloadAcquisition
{
    Task<DependencyPayloadResult> EnsureAsync(
        OptionalDependencyDefinition definition,
        CancellationToken cancellationToken);

    Task<DependencyPayloadResult> InstallAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken);
}

public readonly record struct DependencyPayloadResult(bool Satisfied, string Message, bool Changed = false);
