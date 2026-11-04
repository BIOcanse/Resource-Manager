using System.Runtime.ExceptionServices;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

/// <summary>Owns every process object returned by a process enumeration through all exits.</summary>
internal static class ProcessCollectionOwner
{
    internal static TResult Use<TProcess, TResult>(TProcess[] processes, Func<TProcess[], TResult> read)
        where TProcess : IDisposable
    {
        ArgumentNullException.ThrowIfNull(processes);
        TResult result = default!;
        ExceptionDispatchInfo? failure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(read);
            result = read(processes);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        foreach (var process in processes)
        {
            try
            {
                process.Dispose();
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        failure?.Throw();
        return result;
    }
}
