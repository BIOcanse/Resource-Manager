namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal sealed class SamplingOwnerToken
{
    private readonly object gate = new();
    private int active = 1;

    internal SamplingOwnerToken(
        ulong workspaceIncarnation,
        ulong configurationGeneration)
    {
        if (workspaceIncarnation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workspaceIncarnation));
        }
        if (configurationGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configurationGeneration));
        }

        WorkspaceIncarnation = workspaceIncarnation;
        ConfigurationGeneration = configurationGeneration;
    }

    internal ulong WorkspaceIncarnation { get; }

    internal ulong ConfigurationGeneration { get; }

    internal bool IsActive => Volatile.Read(ref active) != 0;

    internal bool TryPublish(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (gate)
        {
            if (active == 0)
            {
                return false;
            }

            commit();
            return true;
        }
    }

    internal void Revoke()
    {
        lock (gate)
        {
            Volatile.Write(ref active, 0);
        }
    }
}
