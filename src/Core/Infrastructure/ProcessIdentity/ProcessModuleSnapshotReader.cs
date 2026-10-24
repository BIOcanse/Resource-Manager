using System.Runtime.ExceptionServices;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

/// <summary>Copies facts out of owned process modules, then disposes every module from the enumeration.</summary>
internal static class ProcessModuleSnapshotReader
{
    internal static IReadOnlyList<TResult> ReadAndDispose<TModule, TResult>(
        IEnumerable<TModule> modules,
        Func<TModule, TResult> read)
        where TModule : notnull, IDisposable
    {
        ArgumentNullException.ThrowIfNull(modules);

        var owned = new List<TModule>();
        ExceptionDispatchInfo? failure = null;
        try
        {
            foreach (var module in modules)
            {
                owned.Add(module);
            }
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }

        if (read is null)
            failure ??= ExceptionDispatchInfo.Capture(new ArgumentNullException(nameof(read)));

        var snapshots = new List<TResult>(owned.Count);
        foreach (var module in owned)
        {
            try
            {
                if (failure is null && read is not null)
                {
                    snapshots.Add(read(module));
                }
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                try
                {
                    module.Dispose();
                }
                catch (Exception error)
                {
                    failure ??= ExceptionDispatchInfo.Capture(error);
                }
            }
        }

        failure?.Throw();
        return snapshots.AsReadOnly();
    }
}
