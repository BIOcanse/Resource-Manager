namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal static class HostManagerBestEffortCleanup
{
    public static IReadOnlyList<Exception> DisposeAll(params IDisposable?[] disposables)
    {
        List<Exception>? failures = null;
        foreach (var disposable in disposables)
        {
            if (disposable is null)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures ?? [];
    }
}
