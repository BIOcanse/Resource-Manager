using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>Caller-visible finite limits for one process-details capture.</summary>
public sealed record WindowsProcessDetailsOptions
{
    public static WindowsProcessDetailsOptions Default { get; } = new();

    public int MaximumProcesses { get; }
    public int InitialModulesPerProcess { get; }
    public int MaximumModulesPerProcess { get; }
    public int MaximumModuleEnumerationAttempts { get; }
    public int InitialSystemInformationBufferBytes { get; }
    public int MaximumSystemInformationBufferBytes { get; }
    public int MaximumSystemInformationAttempts { get; }
    public int MaximumThreadsPerProcess { get; }
    public int MaximumImagePathCharacters { get; }

    public WindowsProcessDetailsOptions(
        int maximumProcesses = 131072,
        int initialModulesPerProcess = 64,
        int maximumModulesPerProcess = 8192,
        int maximumModuleEnumerationAttempts = 8,
        int initialSystemInformationBufferBytes = 256 * 1024,
        int maximumSystemInformationBufferBytes = 64 * 1024 * 1024,
        int maximumSystemInformationAttempts = 9,
        int maximumThreadsPerProcess = 262144,
        int maximumImagePathCharacters = 32768)
    {
        RequirePositiveArrayLength(maximumProcesses, nameof(maximumProcesses));
        RequirePositiveArrayLength(initialModulesPerProcess, nameof(initialModulesPerProcess));
        RequirePositiveArrayLength(maximumModulesPerProcess, nameof(maximumModulesPerProcess));
        RequirePositiveArrayLength(maximumModuleEnumerationAttempts, nameof(maximumModuleEnumerationAttempts));
        RequirePositiveArrayLength(initialSystemInformationBufferBytes, nameof(initialSystemInformationBufferBytes));
        RequirePositiveArrayLength(maximumSystemInformationBufferBytes, nameof(maximumSystemInformationBufferBytes));
        RequirePositiveArrayLength(maximumSystemInformationAttempts, nameof(maximumSystemInformationAttempts));
        RequirePositiveArrayLength(maximumThreadsPerProcess, nameof(maximumThreadsPerProcess));
        RequirePositiveArrayLength(maximumImagePathCharacters, nameof(maximumImagePathCharacters));

        var moduleArrayBytes = checked((long)maximumModulesPerProcess * IntPtr.Size);
        if (moduleArrayBytes > Array.MaxLength || moduleArrayBytes > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumModulesPerProcess), "Module arrays must fit both managed and native byte lengths.");
        if (initialModulesPerProcess > maximumModulesPerProcess)
            throw new ArgumentOutOfRangeException(nameof(initialModulesPerProcess), "Initial module capacity cannot exceed its maximum.");

        var threadArrayBytes = checked((long)maximumThreadsPerProcess * WindowsProcessSystemThreadParser.ThreadInformationSize);
        if (threadArrayBytes > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maximumThreadsPerProcess), "Thread records must fit a managed array length.");

        var maximumPathBytes = checked((long)maximumImagePathCharacters * sizeof(char));
        if (maximumPathBytes > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maximumImagePathCharacters), "Image path buffers must fit managed and native lengths.");

        if (initialSystemInformationBufferBytes > maximumSystemInformationBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(initialSystemInformationBufferBytes), "The initial system-information buffer cannot exceed its maximum.");

        MaximumProcesses = maximumProcesses;
        InitialModulesPerProcess = initialModulesPerProcess;
        MaximumModulesPerProcess = maximumModulesPerProcess;
        MaximumModuleEnumerationAttempts = maximumModuleEnumerationAttempts;
        InitialSystemInformationBufferBytes = initialSystemInformationBufferBytes;
        MaximumSystemInformationBufferBytes = maximumSystemInformationBufferBytes;
        MaximumSystemInformationAttempts = maximumSystemInformationAttempts;
        MaximumThreadsPerProcess = maximumThreadsPerProcess;
        MaximumImagePathCharacters = maximumImagePathCharacters;
    }

    private static void RequirePositiveArrayLength(int value, string parameterName)
    {
        if (value <= 0 || value > Array.MaxLength)
            throw new ArgumentOutOfRangeException(parameterName, "Limits must be positive and representable as managed array lengths.");
    }
}
