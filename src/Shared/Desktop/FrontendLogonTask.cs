using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace ResourceManager.Shared.Desktop;

public enum FrontendLogonTaskState
{
    /// <summary>The product's task, and Task Scheduler will start it elevated at the user's logon.</summary>
    Active,
    /// <summary>The product's task, but disabled, without an enabled logon trigger or without the highest run level.</summary>
    Inactive,
    /// <summary>Any other content under the name. It is never overwritten or deleted.</summary>
    Foreign
}

/// <summary>
/// The per-user Task Scheduler logon task that starts the bundled frontend in the background with the user's
/// elevated token. The frontend manifest requires an elevated administrator, and Windows does not start such a
/// program from a Run value, so this task is the single persisted logon-startup truth.
/// </summary>
public static class FrontendLogonTask
{
    public const string NamePrefix = "ResourceManager.FrontendLogon.";
    public const string Arguments = "--background-startup";
    // Task Scheduler priority 4 is NORMAL_PRIORITY_CLASS, the priority Explorer gave the former Run value;
    // the scheduler default (7) is below normal.
    internal const int Priority = 4;
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string NameFor(string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        if (!sid.IsAccountSid() || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || !string.Equals(sid.Value, userSid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The logon task needs the user's account SID.", nameof(userSid));
        return NamePrefix + sid.Value;
    }

    public static string BuildDefinition(string executablePath, string userSid)
    {
        var path = RequireExecutable(executablePath);
        _ = NameFor(userSid);
        var document = new XDocument(new XElement(TaskNs + "Task", new XAttribute("version", "1.2"),
            new XElement(TaskNs + "RegistrationInfo",
                new XElement(TaskNs + "Author", "Resource Manager"),
                new XElement(TaskNs + "Description", "Starts the Resource Manager interface in the background when this user signs in.")),
            new XElement(TaskNs + "Triggers",
                new XElement(TaskNs + "LogonTrigger",
                    new XElement(TaskNs + "Enabled", "true"),
                    new XElement(TaskNs + "UserId", userSid))),
            new XElement(TaskNs + "Principals",
                new XElement(TaskNs + "Principal", new XAttribute("id", "Author"),
                    new XElement(TaskNs + "UserId", userSid),
                    new XElement(TaskNs + "LogonType", "InteractiveToken"),
                    new XElement(TaskNs + "RunLevel", "HighestAvailable"))),
            new XElement(TaskNs + "Settings",
                new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
                new XElement(TaskNs + "AllowHardTerminate", "true"),
                new XElement(TaskNs + "StartWhenAvailable", "false"),
                new XElement(TaskNs + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(TaskNs + "AllowStartOnDemand", "true"),
                new XElement(TaskNs + "Enabled", "true"),
                new XElement(TaskNs + "Hidden", "false"),
                new XElement(TaskNs + "RunOnlyIfIdle", "false"),
                new XElement(TaskNs + "WakeToRun", "false"),
                new XElement(TaskNs + "ExecutionTimeLimit", "PT0S"),
                new XElement(TaskNs + "Priority", Priority.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new XElement(TaskNs + "Actions", new XAttribute("Context", "Author"),
                new XElement(TaskNs + "Exec",
                    new XElement(TaskNs + "Command", path),
                    new XElement(TaskNs + "Arguments", Arguments),
                    new XElement(TaskNs + "WorkingDirectory", Path.GetDirectoryName(path)!)))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Classifies a registered definition against the given frontend executable.</summary>
    public static FrontendLogonTaskState Inspect(string definitionXml, string executablePath)
    {
        var path = RequireExecutable(executablePath);
        XDocument document;
        try { document = XDocument.Parse(definitionXml); }
        catch (XmlException) { return FrontendLogonTaskState.Foreign; }
        var root = document.Root;
        if (root is null || root.Name != TaskNs + "Task") return FrontendLogonTaskState.Foreign;
        var actions = root.Element(TaskNs + "Actions")?.Elements().ToArray() ?? [];
        if (actions.Length != 1 || actions[0].Name != TaskNs + "Exec") return FrontendLogonTaskState.Foreign;
        var command = actions[0].Element(TaskNs + "Command")?.Value.Trim().Trim('"');
        var arguments = actions[0].Element(TaskNs + "Arguments")?.Value.Trim();
        if (string.IsNullOrEmpty(command) || !Path.IsPathFullyQualified(command)
            || !string.Equals(Path.GetFullPath(command), path, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(arguments, Arguments, StringComparison.Ordinal))
            return FrontendLogonTaskState.Foreign;

        // Task Scheduler may omit elements that hold their default value; Enabled defaults to true.
        var settingsEnabled = !IsFalse(root.Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled"));
        var logonTriggerEnabled = root.Element(TaskNs + "Triggers")?.Elements(TaskNs + "LogonTrigger")
            .Any(trigger => !IsFalse(trigger.Element(TaskNs + "Enabled"))) == true;
        var highest = root.Element(TaskNs + "Principals")?.Elements(TaskNs + "Principal")
            .Any(principal => principal.Element(TaskNs + "RunLevel")?.Value.Trim() == "HighestAvailable") == true;
        return settingsEnabled && logonTriggerEnabled && highest
            ? FrontendLogonTaskState.Active
            : FrontendLogonTaskState.Inactive;
    }

    /// <summary>
    /// Uninstall: deletes every user's logon task that starts this frontend executable. Tasks under the prefix
    /// that start anything else (another installation) and all other tasks are left untouched.
    /// </summary>
    public static IReadOnlyList<string> RemoveAllFor(ITaskSchedulerRootFolder tasks, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var path = RequireExecutable(executablePath);
        var removed = new List<string>();
        foreach (var name in tasks.ListTaskNames())
        {
            if (!name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var definition = tasks.ReadDefinition(name);
            if (definition is null || Inspect(definition, path) == FrontendLogonTaskState.Foreign) continue;
            tasks.Delete(name);
            if (tasks.ReadDefinition(name) is not null)
                throw new IOException("A frontend logon task remained after deletion: " + name);
            removed.Add(name);
        }
        return removed;
    }

    private static bool IsFalse(XElement? element) =>
        element is not null && string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string RequireExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"')
            || !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("The logon task needs the frontend's full executable path.", nameof(executablePath));
        return Path.GetFullPath(executablePath);
    }
}
