namespace ResourceManager.App.Hosting.StartupCapabilities;

public static class StartupProfileCompiler
{
    private const string ProfileOption = "--startup-profile";

    public static StartupCapabilitySet Compile(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? selectedProfile = null;
        var index = 0;
        while (index < args.Count)
        {
            var argument = args[index];
            string? candidate = null;
            if (argument.StartsWith($"{ProfileOption}=", StringComparison.Ordinal))
            {
                candidate = argument[(ProfileOption.Length + 1)..];
            }
            else if (string.Equals(argument, ProfileOption, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException(
                        $"{ProfileOption} requires an explicit profile value.",
                        nameof(args));
                }

                candidate = args[++index];
            }

            if (candidate is not null)
            {
                if (selectedProfile is not null)
                {
                    throw new ArgumentException(
                        $"{ProfileOption} may be specified only once.",
                        nameof(args));
                }

                selectedProfile = candidate;
            }

            index++;
        }

        return selectedProfile?.Trim().ToLowerInvariant() switch
        {
            StartupCapabilitySet.NormalReadOnlyProfileId => StartupCapabilitySet.NormalReadOnly,
            null or "" or StartupCapabilitySet.FullProfileId => StartupCapabilitySet.Full,
            _ => throw new ArgumentException(
                $"Unsupported startup profile '{selectedProfile}'.",
                nameof(args))
        };
    }
}
