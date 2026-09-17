using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 配置这一摊。
///
/// **这里的任何操作都不会动硬件。** 点一份配置是把内容载入草稿（那一步在前端），
/// 落到硬件由用户点「应用」，走期望状态机那条路。
/// </summary>
public sealed class ControlPresets(IControlPresetStore store) : IControlPresets
{
    /// <summary>
    /// 配置最多存多少份。
    ///
    /// 这不是省空间，是止损：这个文件在每次保存时整份重写，
    /// 没有上限的话，一个循环调用保存的脚本能把它撑到读不动。
    /// </summary>
    private const int MaximumPresetCount = 64;

    private const int MaximumNameLength = 64;

    public Task<ControlPresetCatalog> ReadAsync(CancellationToken cancellationToken)
        => store.LoadAsync(cancellationToken);

    public async Task<ControlPresetCatalog> SaveAsync(
        string name,
        ControlDesiredState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumNameLength)
        {
            throw new ArgumentException($"配置的名字要有，并且不超过 {MaximumNameLength} 个字。");
        }

        var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var existing = catalog.Presets.FirstOrDefault(entry =>
            string.Equals(entry.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        List<ControlPreset> presets;
        if (existing is not null)
        {
            // 同名覆盖，**id 不变** —— 用户再存一次"游戏"是想更新那一份，
            // 而不是攒出两个都叫"游戏"的东西。
            presets = catalog.Presets
                .Select(entry => ReferenceEquals(entry, existing)
                    ? entry with { Desired = desired, UpdatedAt = now }
                    : entry)
                .ToList();
        }
        else
        {
            if (catalog.Presets.Count >= MaximumPresetCount)
            {
                throw new InvalidOperationException(
                    $"配置最多存 {MaximumPresetCount} 份，先删掉一些再存。");
            }
            presets = [.. catalog.Presets, new ControlPreset(
                Guid.NewGuid().ToString("n"),
                trimmed,
                desired,
                now,
                now)];
        }

        var next = new ControlPresetCatalog(presets);
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return next;
    }

    public async Task<ControlPresetCatalog> DeleteAsync(
        string presetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var next = new ControlPresetCatalog(catalog.Presets
            .Where(entry => !string.Equals(entry.Id, presetId, StringComparison.Ordinal))
            .ToArray());
        // 删掉一份配置**不动期望状态**：机器现在保持成什么样，和我攒了哪几套方案，
        // 是两件没有关系的事。
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return next;
    }
}
