using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

/// <summary>
/// 把"目录里的一个依赖 + 用户选的版本"解析成"这次可以下载的具体安装器地址"。
/// </summary>
public interface IDependencyInstallerSourceResolver
{
    /// <summary>
    /// 列出版本对话框要显示的选项。已验证版本是确定性拼出来的，不联网；最新版本需要
    /// 调用上游 API，失败时该项标记为不可用并带上原因，其余选项不受影响。
    /// </summary>
    Task<DependencyVersionOptions> GetVersionOptionsAsync(
        OptionalDependencyDefinition definition,
        CancellationToken cancellationToken);

    /// <summary>
    /// 解析指定版本选择的下载地址。<paramref name="versionChoice"/> 为 null 时按
    /// <see cref="DependencyVersionChoices.Verified"/> 处理。
    /// </summary>
    Task<ResolvedInstallerSource> ResolveAsync(
        OptionalDependencyDefinition definition,
        string? versionChoice,
        CancellationToken cancellationToken);
}
