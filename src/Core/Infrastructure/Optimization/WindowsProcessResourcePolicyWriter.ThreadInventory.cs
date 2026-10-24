using ResourceManager.App.Application.Optimization;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    private static RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>> ReadIdealThreadInventory()
    {
        // Reuse the process-details reader's bounded native transaction and validated table layout.
        // There is no retained inventory owner or snapshot fallback on this path.
        var options = WindowsProcessDetailsOptions.Default;
        var api = new WindowsProcessDetailsNativeApi(options);
        if (!api.TryReadSystemProcessInformation(out var bytes)
            || !WindowsProcessSystemThreadParser.TryParsePlacementInventory(bytes, options, out var rows))
            return RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>.Unavailable(
                0, "The bounded system thread inventory could not be read or validated.");
        IReadOnlyDictionary<int, IReadOnlyList<int>> inventory = rows.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<int>)pair.Value.Select(static thread => unchecked((int)thread.ThreadId)).ToArray());
        return RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>.Found(inventory);
    }
}
