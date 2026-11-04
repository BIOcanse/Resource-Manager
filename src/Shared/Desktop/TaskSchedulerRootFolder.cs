using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Shared.Desktop;

/// <summary>The Task Scheduler root folder, reduced to what the frontend logon task needs.</summary>
public interface ITaskSchedulerRootFolder
{
    /// <summary>The registered task's XML, or null when no task has this name.</summary>
    string? ReadDefinition(string taskName);

    /// <summary>Names of the tasks registered directly in the root folder, hidden ones included.</summary>
    IReadOnlyList<string> ListTaskNames();

    /// <summary>Creates or replaces the task from its XML; the XML's principal runs it.</summary>
    void Register(string taskName, string definitionXml);

    /// <summary>Deletes the task. A task that is already absent is not an error.</summary>
    void Delete(string taskName);
}

/// <summary>
/// Late-bound (IDispatch) use of the Task Scheduler 2.0 service, as PowerShell's ScheduledTasks module uses it.
/// Failures surface as IOException or UnauthorizedAccessException; nothing is retried or substituted.
/// </summary>
public sealed class WindowsTaskSchedulerRootFolder : ITaskSchedulerRootFolder
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskEnumHidden = 1;
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int MaximumListedTasks = 100_000;

    public string? ReadDefinition(string taskName) => WithRoot<string?>(root =>
    {
        object? task;
        try { task = Invoke(root, "GetTask", taskName); }
        catch (Exception error) when (error.HResult == FileNotFound) { return null; }
        try { return Get(task!, "Xml") as string ?? throw new IOException("Task Scheduler returned no task definition."); }
        finally { Release(task); }
    });

    public IReadOnlyList<string> ListTaskNames() => WithRoot<IReadOnlyList<string>>(root =>
    {
        var collection = Invoke(root, "GetTasks", TaskEnumHidden)!;
        try
        {
            var count = Convert.ToInt32(Get(collection, "Count"), System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 0 or > MaximumListedTasks) throw new IOException("The Task Scheduler root folder exceeds its listing bound.");
            var names = new List<string>(count);
            for (var index = 1; index <= count; index++)
            {
                var task = Get(collection, "Item", index);
                try { names.Add(Get(task!, "Name") as string ?? throw new IOException("Task Scheduler returned a task without a name.")); }
                finally { Release(task); }
            }
            return (IReadOnlyList<string>)names;
        }
        finally { Release(collection); }
    });

    public void Register(string taskName, string definitionXml) => WithRoot(root =>
    {
        // userId and password stay empty: the XML principal (the user's SID, interactive token) runs the task.
        Release(Invoke(root, "RegisterTask", taskName, definitionXml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken));
        return 0;
    });

    public void Delete(string taskName) => WithRoot(root =>
    {
        try { Invoke(root, "DeleteTask", taskName, 0); }
        catch (Exception error) when (error.HResult == FileNotFound) { }
        return 0;
    });

    private static T WithRoot<T>(Func<object, T> use)
    {
        object? service = null;
        object? root = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type) ?? throw new IOException("Task Scheduler is unavailable.");
            Invoke(service, "Connect");
            root = Invoke(service, "GetFolder", "\\") ?? throw new IOException("Task Scheduler has no root folder.");
            return use(root);
        }
        catch (Exception error) when (error is COMException or InvalidComObjectException or MissingMemberException
            or TypeLoadException or NotSupportedException)
        {
            throw new IOException($"Task Scheduler failed (0x{error.HResult:X8}).", error);
        }
        finally
        {
            Release(root);
            Release(service);
        }
    }

    private static object? Invoke(object target, string method, params object?[] arguments)
    {
        try { return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
    }

    private static object? Get(object target, string property, params object?[] index)
    {
        try { return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, index); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
