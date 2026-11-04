using System.Globalization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal readonly record struct ProcessInstanceText(ProcessInstanceKey Key, string? CommandLine, string? UserSid);

/// <summary>One capture's temporary index over the existing current/attribution identities.
/// No successful text is retained outside those original owners.</summary>
internal sealed class ProcessInstanceTextSharing(Func<IEnumerable<ProcessInstanceText>> readKnown)
{
    private Dictionary<ProcessInstanceKey, string>? commands;
    private Dictionary<string, string>? sids;

    private void Initialize()
    {
        if (commands is not null) return;
        commands = [];
        sids = new(StringComparer.Ordinal);
        foreach (var text in readKnown())
        {
            if (text.CommandLine is not null) commands.TryAdd(text.Key, text.CommandLine);
            if (text.UserSid is { } sid) sids.TryAdd(sid, sid);
        }
    }

    internal string? ShareCommandLine(ProcessInstanceKey key, string? value)
    {
        if (value is null) return null;
        Initialize();
        if (commands!.TryGetValue(key, out var known) && string.Equals(known, value, StringComparison.Ordinal)) return known;
        commands[key] = value;
        return value;
    }

    internal ProcessBasicMetricObservation Share(ProcessInstanceKey key, ProcessBasicMetricObservation value)
    {
        if (value.CommandLine is null && value.UserSid is null) return value;
        Initialize();
        var command = ShareCommandLine(key, value.CommandLine);
        var sid = ShareUserSid(value.UserSid);
        return ReferenceEquals(command, value.CommandLine) && ReferenceEquals(sid, value.UserSid)
            ? value : value with { CommandLine = command, UserSid = sid };
    }

    internal string? ShareUserSid(string? sid)
    {
        if (sid is not null)
        {
            Initialize();
            if (sids!.TryGetValue(sid, out var known)) sid = known;
            else sids.Add(sid, sid);
        }
        return sid;
    }
}

public sealed partial class WindowsProcessFieldOwner
{
    internal Func<IEnumerable<ProcessInstanceText>>? ReadAttributionText { get; set; }
    internal ProcessInstanceTextSharing CreateTextSharing() => new(ReadKnownText);

    private IEnumerable<ProcessInstanceText> ReadKnownText()
    {
        // Prefer the same authoritative canonical fields the external reader sees.
        if (Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics) is { } part)
        {
            foreach (var process in part.Processes)
            {
                if (!long.TryParse(process.CreationWindowsFileTime100Ns, NumberStyles.None, CultureInfo.InvariantCulture, out var creation)
                    || process.ProcessId <= 0 || creation <= 0) continue;
                string? command = null, sid = null;
                foreach (var value in process.Values)
                {
                    if (value.Field == "process_all_command_line_actual") command = value.StringValue;
                    else if (value.Field == "process_all_user_sid_actual") sid = value.StringValue;
                }
                if (command is not null || sid is not null) yield return new(new(process.ProcessId, creation), command, sid);
            }
        }
        if (ReadAttributionText is { } read)
            foreach (var text in read()) yield return text;
    }
}
