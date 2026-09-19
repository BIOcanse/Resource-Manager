using System.Text.Json;
using ResourceManager.App.Application.Software;

namespace ResourceManager.App.Infrastructure.Software;

/// <summary>
/// 软件策略档案：**随发行版发出去的那一份标准配置，就这一份。**
///
/// 它打在程序集里，解压即用 —— 这是便携软件，没有安装期，
/// 也就没有"用户那份和自带那份打架"这种事，不需要导入、合并或覆盖。
///
/// 先前这里读的是 <c>UserData/SoftwareProfiles/game-mode.local.json</c>，
/// 那个目录在 .gitignore 里、也不随发行版走，于是任何一份新解压出来的程序
/// **一条策略都没有**：分组、状态矩阵、名单全是空的。
///
/// 档案里那份 <c>softwareAssignments</c> 也是配置的一部分：它的
/// <c>softwareId</c> 是产品级的卸载注册表键（装在哪台机器上都一样），
/// 所以带得走。各机器的安装路径不带 —— 那个在本机发现时自己查。
/// </summary>
public sealed class JsonSoftwarePolicyProfileProvider : ISoftwarePolicyProfileProvider
{
    private const string ProfileResourceName =
        "ResourceManager.Configuration.SoftwareProfiles.official.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 读一次就够：它是随程序集发出来的，运行期间不会变。
    /// </summary>
    private static readonly Lazy<SoftwarePolicyProfile> Profile = new(Load);

    public Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken)
        => Task.FromResult(Profile.Value);

    private static SoftwarePolicyProfile Load()
    {
        using var stream = typeof(JsonSoftwarePolicyProfileProvider).Assembly
            .GetManifestResourceStream(ProfileResourceName);
        if (stream is null)
        {
            // 读不出来只有一种可能：打包漏了它。这不是"这台机器没配"，是发行版坏了。
            return SoftwarePolicyProfile.Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<SoftwarePolicyProfileDocument>(
                stream,
                JsonOptions);
            return document is null ? SoftwarePolicyProfile.Empty : ToProfile(document);
        }
        catch (JsonException)
        {
            return SoftwarePolicyProfile.Empty;
        }
    }

    private static SoftwarePolicyProfile ToProfile(SoftwarePolicyProfileDocument document)
    {
        var groups = (document.SoftwareGroups ?? [])
            .Where(static group => !string.IsNullOrWhiteSpace(group.GroupId))
            .Select(static group => new SoftwarePolicyGroup(
                group.GroupId!.Trim(),
                string.IsNullOrWhiteSpace(group.DisplayName) ? group.GroupId!.Trim() : group.DisplayName!.Trim(),
                string.IsNullOrWhiteSpace(group.Role) ? null : group.Role!.Trim(),
                CleanStrings(group.Capabilities)))
            .GroupBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var assignments = (document.SoftwareAssignments ?? [])
            .Where(static assignment => !string.IsNullOrWhiteSpace(assignment.SoftwareId)
                && !string.IsNullOrWhiteSpace(assignment.AssignedGroupId))
            .Select(static assignment => new SoftwarePolicyAssignment(
                assignment.SoftwareId!.Trim(),
                string.IsNullOrWhiteSpace(assignment.DisplayName) ? assignment.SoftwareId!.Trim() : assignment.DisplayName!.Trim(),
                assignment.AssignedGroupId!.Trim(),
                CleanStrings(assignment.RootPaths)))
            .GroupBy(static assignment => assignment.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new SoftwarePolicyProfile(groups, assignments);
    }

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? values)
    {
        return (values ?? [])
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class SoftwarePolicyProfileDocument
    {
        public IReadOnlyList<SoftwarePolicyGroupDocument>? SoftwareGroups { get; set; }
        public IReadOnlyList<SoftwarePolicyAssignmentDocument>? SoftwareAssignments { get; set; }
    }

    private sealed class SoftwarePolicyGroupDocument
    {
        public string? GroupId { get; set; }
        public string? DisplayName { get; set; }
        public string? Role { get; set; }
        public IReadOnlyList<string>? Capabilities { get; set; }
    }

    private sealed class SoftwarePolicyAssignmentDocument
    {
        public string? SoftwareId { get; set; }
        public string? DisplayName { get; set; }
        public string? AssignedGroupId { get; set; }
        public IReadOnlyList<string>? RootPaths { get; set; }
    }
}
